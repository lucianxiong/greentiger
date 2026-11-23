using System;
using System.Collections.Generic;
using System.Text;

namespace GreenTiger
{
    /// <summary>
    /// 「可继续任务」暂存区。单会话只保留一份活动任务记录，新任务会顶掉旧的；
    /// 被顶掉的任务如果还能续跑，就挪到这里，之后可以按 taskId 取回来继续——
    /// 不做多任务并行，只保证「暂停的东西不会因为顺手问个别的问题就永久丢失」。
    /// </summary>
    public static class TaskParking
    {
        public const int Max = 5;

        public static List<TaskRecord> List(Session session)
        {
            if (session == null) return new List<TaskRecord>();
            if (session.ParkedTasks == null) session.ParkedTasks = new List<TaskRecord>();
            return session.ParkedTasks;
        }

        public static void Park(Session session, TaskRecord task)
        {
            if (session == null || task == null || string.IsNullOrEmpty(task.TaskId)) return;
            List<TaskRecord> list = List(session);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] != null && list[i].TaskId == task.TaskId) list.RemoveAt(i);
            }
            list.Insert(0, task);
            while (list.Count > Max) list.RemoveAt(list.Count - 1);
        }

        public static TaskRecord Find(Session session, string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return null;
            List<TaskRecord> list = List(session);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].TaskId == taskId) return list[i];
            }
            return null;
        }

        public static TaskRecord Take(Session session, string taskId)
        {
            TaskRecord found = Find(session, taskId);
            if (found != null) List(session).Remove(found);
            return found;
        }
    }

    /// <summary>
    /// 任务租约：同一任务同一时刻只允许一个 Worker 推进。
    /// 没有数据库，就用会话 JSON 里的 RunOwner + LeaseUntil 做轻量并发控制；
    /// 租约过期（进程崩溃/被杀）后可由新进程接管，接管前先做状态核验。
    /// </summary>
    public static class TaskLease
    {
        /// <summary>租约时长。任务运行期间定期续租；进程崩溃后最多这么久就能被接管。</summary>
        public const int LeaseMs = 120000;

        private static string _owner;
        /// <summary>
        /// 进程内持有的锁文件句柄。JSON 里的 RunOwner 只是「谁在跑」的记录，
        /// 真正的互斥靠这个句柄：文件以 FileShare.None 打开，同一台机器上第二个进程
        /// 直接 IOException；共享目录（SMB）上由服务器强制 share mode，跨机器也拦得住。
        /// 进程被杀时句柄由操作系统释放，不需要额外清理逻辑。
        /// </summary>
        private static System.IO.FileStream _lockHandle;
        private static string _lockTaskId;

        public static string OwnerId
        {
            get
            {
                if (string.IsNullOrEmpty(_owner))
                {
                    _owner = Environment.MachineName + ":" +
                             System.Diagnostics.Process.GetCurrentProcess().Id + ":" +
                             Guid.NewGuid().ToString("N").Substring(0, 6);
                }
                return _owner;
            }
        }

        /// <summary>
        /// 尝试取得任务所有权。已有未过期租约且属于别的 Worker 时拒绝（防止双 Worker 抢同一任务）。
        /// </summary>
        public static bool TryAcquire(TaskRecord task, out string reason)
        {
            reason = "";
            if (task == null)
            {
                reason = "任务记录为空";
                return false;
            }
            long now = Message.Now();
            if (!string.IsNullOrEmpty(task.RunOwner) &&
                task.RunOwner != OwnerId &&
                task.LeaseUntil > now)
            {
                reason = "该任务正被另一个执行实例（" + task.RunOwner + "）持有，租约剩余 " +
                         ((task.LeaseUntil - now) / 1000) + " 秒。";
                return false;
            }
            // 真正的互斥：独占打开锁文件。拿不到就是别人（可能是另一台机器）正在跑。
            string lockError;
            if (!TryLockFile(task.TaskId, out lockError))
            {
                reason = "该任务正被另一个进程持有文件锁（" + lockError + "），本进程不接管。";
                return false;
            }
            task.RunOwner = OwnerId;
            task.LeaseUntil = now + LeaseMs;
            task.Version++;
            return true;
        }

        /// <summary>锁文件路径（按 taskId 区分，放在 data/locks 下）。</summary>
        public static string LockPath(string taskId)
        {
            string safe = string.IsNullOrEmpty(taskId) ? "unknown" : taskId;
            foreach (char bad in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '_');
            return System.IO.Path.Combine(AppPaths.LocksDir, safe + ".lease");
        }

        private static bool TryLockFile(string taskId, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(taskId)) return true; // 没有 id 的场景（测试/旧数据）不启用文件锁
            if (_lockHandle != null && string.Equals(_lockTaskId, taskId, StringComparison.Ordinal)) return true;
            ReleaseLockFile();
            try
            {
                AppPaths.EnsureDirectory(AppPaths.LocksDir);
                _lockHandle = new System.IO.FileStream(LockPath(taskId), System.IO.FileMode.OpenOrCreate,
                    System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
                _lockTaskId = taskId;
                byte[] info = Encoding.UTF8.GetBytes(OwnerId + " @ " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                _lockHandle.SetLength(0);
                _lockHandle.Write(info, 0, info.Length);
                _lockHandle.Flush();
                return true;
            }
            catch (Exception ex)
            {
                _lockHandle = null;
                _lockTaskId = null;
                // 异常类型本身是判断依据：IOException 多半是「别的 Worker 正持锁」（正常竞争），
                // UnauthorizedAccessException 则说明权限/只读介质出了问题（必须让人看见）。
                // 以前只留 Message，两种完全不同的情况在日志里长得一样。
                error = ex.Message;
                Log.Warn("任务 " + taskId + " 取锁失败: " + Log.Describe(ex));
                return false;
            }
        }

        private static void ReleaseLockFile()
        {
            System.IO.FileStream h = _lockHandle;
            string taskId = _lockTaskId;
            _lockHandle = null;
            _lockTaskId = null;
            if (h == null) return;
            try { h.Dispose(); } catch { }
            try
            {
                if (!string.IsNullOrEmpty(taskId))
                {
                    string path = LockPath(taskId);
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                // 删不掉锁文件不是致命错误（有过期兜底），但会在磁盘上留下陈旧锁，必须留痕。
                Log.Warn("任务 " + taskId + " 的锁文件未能删除，可能残留陈旧锁: " + ex.Message);
            }
        }

        /// <summary>
        /// 续租。返回 false 表示「这个任务已经不归我了」（被别的 Worker 接管），
        /// 调用方必须立刻停止推进并放弃写入——否则两边会同时改同一份任务记录。
        /// </summary>
        public static bool Renew(TaskRecord task)
        {
            if (task == null) return false;
            if (task.RunOwner != OwnerId) return false;
            task.LeaseUntil = Message.Now() + LeaseMs;
            return true;
        }

        /// <summary>当前租约是否仍属于本进程（只读判断，不续租）。</summary>
        public static bool Owns(TaskRecord task)
        {
            return task != null && string.Equals(task.RunOwner, OwnerId, StringComparison.Ordinal);
        }

        public static void Release(TaskRecord task)
        {
            if (task == null) return;
            if (task.RunOwner != OwnerId) return;
            task.RunOwner = "";
            task.LeaseUntil = 0;
            ReleaseLockFile();
        }
    }

    /// <summary>
    /// 启动时的一致性修复：进程重启后，上次没跑完的任务不能就这么悬着。
    /// 处理方式是「如实标记 + 提示核验」，不猜测成功，也不自动重放副作用操作。
    /// </summary>
    public static class SessionRecovery
    {
        /// <summary>返回恢复摘要（null 表示这个会话不需要恢复）。</summary>
        public static string Recover(Session session, string reason)
        {
            return Recover(session, reason, 0);
        }

        /// <summary>ttlSeconds=0 表示用票据默认有效期。</summary>
        public static string Recover(Session session, string reason, int ttlSeconds)
        {
            if (session == null) return null;
            try
            {
                return RecoverUnsafe(session, reason, ttlSeconds);
            }
            catch (Exception ex)
            {
                // 恢复中途炸掉会让会话留在「半恢复」状态：有的调用标了 interrupted、有的没有。
                // 宁可什么都不做，也不要给用户一个自相矛盾的会话，所以这里只如实落日志。
                Log.Error("会话 " + session.Id + " 中断恢复失败", ex);
                return null;
            }
        }

        private static string RecoverUnsafe(Session session, string reason, int ttlSeconds)
        {
            if (session == null) return null;
            // 先把过期票据清掉：隔夜重启后，那张「批准」早就不能用了
            int expired = ApprovalStore.Expire(session, ttlSeconds <= 0 ? ApprovalStore.DefaultTtlSeconds : ttlSeconds);
            TaskRecord task = session.Task;
            bool taskRunning = task != null && task.Outcome == TaskOutcome.Running;
            List<ToolCall> dangling = FindDangling(session);
            if (!taskRunning && dangling.Count == 0) return null;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("【中断恢复】");
            sb.AppendLine("上次任务在执行中被中断（" + (string.IsNullOrEmpty(reason) ? "进程重启" : reason) + "）。");

            int sideEffects = 0;
            bool anyRunning = false;
            for (int i = 0; i < dangling.Count; i++)
            {
                ToolCall call = dangling[i];
                if (call.Status == "running") anyRunning = true;
                call.Status = "interrupted";
                call.ErrorCode = ToolErrorCode.Interrupted;
                call.ErrorClass = ErrorClass.ToolError;
                call.Output = Append(call.Output,
                    "[中断] 进程在工具执行期间退出，结果未知。不要直接重放；先核验目标状态（文件是否存在/远端是否已生效）再决定。");
                ToolSpec spec = ToolRegistry.Get(call.Name);
                if (spec != null && spec.WritesExternal) sideEffects++;
                sb.AppendLine("- 工具「" + call.Name + "」状态未知，已标记 interrupted。");
            }
            if (sideEffects > 0)
            {
                sb.AppendLine("其中 " + sideEffects + " 个是有副作用的调用：请先核验，再决定是否重试。");
            }

            if (task != null)
            {
                string errorClass = sideEffects > 0 ? ErrorClass.ToolError : ErrorClass.Internal;
                // 只卡在「等待审批」的调用没有动过手，语义上是等待状态而不是失败
                string outcome = (!anyRunning && dangling.Count > 0) ? TaskOutcome.WaitingApproval : TaskOutcome.Incomplete;
                task.Finish(outcome, errorClass,
                    "进程中断" + (dangling.Count > 0 ? "，存在状态未知的工具调用" : ""));
                task.Verification = "未执行（任务中断）";
                task.Interrupted = true;
                string resumeWhy;
                task.Resumable = ApprovalStore.CanResume(session, out resumeWhy);
                TaskLease.Release(task);

                ApprovalTicket grantedTicket = ApprovalStore.Granted(session);
                ApprovalTicket pendingTicket = ApprovalStore.Pending(session);
                if (outcome == TaskOutcome.WaitingApproval)
                {
                    if (grantedTicket != null)
                    {
                        sb.AppendLine("审批已通过但尚未执行（票据 " + grantedTicket.Id + "）：点「继续任务」可直接续跑，" +
                                      "票据只对完全相同的调用生效且只能用一次。");
                    }
                    else if (pendingTicket != null)
                    {
                        sb.AppendLine("审批尚未决定（票据 " + pendingTicket.Id + "）：点「继续任务」会重新发起确认，不会自动执行。");
                    }
                    else
                    {
                        sb.AppendLine("该任务当时正在等待审批：点「继续任务」可重新发起，但需要重新确认。");
                    }
                }
                else if (task.Resumable)
                {
                    sb.AppendLine("点「继续任务」可从断点继续；预算、权限规则与验收要求都不变。");
                }
                if (expired > 0) sb.AppendLine("另有 " + expired + " 张过期审批票据已作废。");
            }

            Message note = new Message();
            note.Role = "system";
            note.Status = "error";
            note.Text = sb.ToString().TrimEnd();
            // 只在启动阶段（WebServer 起来之前）调用，此时没有并发读者，不需要加锁
            session.Messages.Add(note);
            session.Touch();

            Trace.Event(task == null ? "" : task.TaskId, "task_recovered", "dangling", dangling.Count);
            Log.Warn("会话 " + session.Id + " 已做中断恢复：悬置调用 " + dangling.Count + " 个");
            return sb.ToString();
        }

        private static List<ToolCall> FindDangling(Session session)
        {
            List<ToolCall> list = new List<ToolCall>();
            if (session.Messages == null) return list;
            for (int i = 0; i < session.Messages.Count; i++)
            {
                List<ToolCall> calls = session.Messages[i].ToolCalls;
                if (calls == null) continue;
                for (int j = 0; j < calls.Count; j++)
                {
                    ToolCall c = calls[j];
                    if (c == null) continue;
                    // 只处理「可能已经动过手」的调用：running（执行中）与 awaiting（已批准待执行）。
                    // pending 表示还没开始，标成中断会误导模型以为副作用可能已发生。
                    if (c.Status == "running" || c.Status == "awaiting") list.Add(c);
                }
            }
            return list;
        }

        private static string Append(string text, string note)
        {
            if (string.IsNullOrEmpty(text)) return note;
            return text + "\n" + note;
        }
    }
}
