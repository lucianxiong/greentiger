using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GreenTiger
{
    /// <summary>
    /// Agent 循环对宿主的最小依赖面。
    /// 之前 AgentRunner 直接依赖 App，导致「构造 App 会起 HTTP/MCP/托盘」，循环逻辑无法被测试。
    /// 抽出接口后：生产用 App，测试用 TestAgentHost（全内存），循环逻辑本身不变。
    /// </summary>
    public interface IAgentHost
    {
        object StateLock { get; }
        Session ActiveSession { get; }
        ToolExecutor Tools { get; }
        Guard Guard { get; }
        McpHost Mcp { get; }
        BrainRouter Brains { get; }
        bool CircuitBroken { get; set; }

        void AddLog(string level, string text);
        void SetPhase(string phase);
        string GetPhase();
        void SetRunning(bool running);
        void BroadcastState();
        void BroadcastAssistant(Message message);
        void SaveActiveSession();

        void ResetAlwaysAllowed();
        bool IsAlwaysAllowed(string toolName);
        Task<string> RequestApprovalAsync(ToolCall call, CancellationToken token);
        /// <summary>取走最近一次审批携带的用户回答（ask_user 选项卡专用；取走即清空）。</summary>
        string TakeApprovalAnswer();
        /// <summary>任务终态系统通知（托盘气泡；config.Notify 控制，长任务才提醒）。</summary>
        void NotifyTaskEnd(TaskRecord task, string userText);
        /// <summary>等待用户确认的系统通知（托盘气泡）。</summary>
        void NotifyApprovalNeeded(string toolName);

        void NewSession();
        string ExportMarkdown(string id);
        Artifact CreateArtifact(string path, string mime, string kind);
        /// <summary>把工作目录里的文件回滚到改动前快照（/undo 用；校验在 App 实现里）。</summary>
        string RestoreBackup(string snapshotPath, string targetPath, out string error);
        /// <summary>把工作目录恢复到某个 shell checkpoint（/undo 与界面「回滚到命令前」用）。</summary>
        string RestoreCheckpoint(string manifestPath, out string error);
        void RegisterProcess(Process process);
        void UnregisterProcess(Process process);
    }
}
