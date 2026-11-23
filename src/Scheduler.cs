using System;
using System.Collections.Generic;
using System.Threading;

namespace GreenTiger
{
    /// <summary>
    /// 定时任务调度器（对齐 DeepSeek Harness 的 Scheduled tasks）。
    /// 每分钟检查一次启用的任务：
    /// - "HH:mm" 每天指定时刻触发一次（例如 "09:00"）；
    /// - "every:N" 每 N 分钟触发一次（例如 "every:30"）。
    /// 触发时通过 App 新建会话并把任务的 Prompt 交给 Agent 执行；
    /// 涉及工具调用时的确认规则与普通任务一致（用户在场会看到确认弹窗）。
    /// </summary>
    public class TaskScheduler : IDisposable
    {
        private readonly App _app;
        private readonly Timer _timer;
        private volatile bool _busy;

        public TaskScheduler(App app)
        {
            _app = app;
            _timer = new Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            try { _timer.Change(15000, 60000); } catch { }
            Log.Info("定时任务调度器已启动（每分钟检查一次）");
        }

        public void Stop()
        {
            try { _timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        }

        private void Tick(object state)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                List<ScheduledTask> tasks = _app.SnapshotScheduledTasks();
                if (tasks == null || tasks.Count == 0) return;
                long now = Message.Now();
                for (int i = 0; i < tasks.Count; i++)
                {
                    ScheduledTask t = tasks[i];
                    if (t == null || !t.Enabled) continue;
                    if (string.IsNullOrEmpty(t.Prompt)) continue;
                    if (!Due(t, now)) continue;
                    _app.RunScheduledTask(t, now);
                }
            }
            catch (Exception ex)
            {
                Log.Error("定时任务检查失败", ex);
            }
            finally
            {
                _busy = false;
            }
        }

        private static bool Due(ScheduledTask t, long now)
        {
            string s = (t.Schedule == null ? "" : t.Schedule.Trim());
            if (s.Length == 0) return false;
            if (s.StartsWith("every:", StringComparison.OrdinalIgnoreCase))
            {
                int minutes;
                if (!int.TryParse(s.Substring(6).Trim(), out minutes)) return false;
                if (minutes <= 0) return false;
                long interval = minutes * 60000L;
                long last = t.LastRunAt;
                if (last <= 0) return true;               // 从未运行过：立即触发一次
                return now - last >= interval;
            }
            // 每天 HH:mm
            string[] parts = s.Split(new char[] { ':' }, 2);
            if (parts.Length != 2) return false;
            int hh, mm;
            if (!int.TryParse(parts[0].Trim(), out hh) || !int.TryParse(parts[1].Trim(), out mm)) return false;
            DateTime nowDt = TimeUtil.FromUnix(now);
            DateTime target = nowDt == DateTime.MinValue ? DateTime.MinValue : nowDt.Date.AddHours(hh).AddMinutes(mm);
            // 补跑语义：只要求「今天该时刻已过且今天没跑过」。
            // 原来要求分钟恰好相等，进程休眠或 tick 抖动错过那一分钟就整天不再触发。
            if (nowDt < target) return false;
            DateTime lastDt = t.LastRunAt <= 0
                ? DateTime.MinValue
                : TimeUtil.FromUnix(t.LastRunAt);
            return lastDt.Date != nowDt.Date;
        }

        public void Dispose()
        {
            Stop();
            try { _timer.Dispose(); } catch { }
        }
    }
}
