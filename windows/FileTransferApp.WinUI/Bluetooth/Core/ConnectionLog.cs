namespace FileTransferApp.WinUI.Bluetooth.Core;

/// <summary>
/// 连接诊断日志：静态事件总线，任何层都可打点，UI 层订阅后转投日志区。
/// 每条带时间戳，用于回答"失败发生在哪一步"（Radio / SDP / Connect / Listener / 恢复动作）。
/// </summary>
internal static class ConnectionLog
{
    public static event Action<string>? Entry;

    public static void Write(string step, string detail = "") =>
        Entry?.Invoke($"[{DateTime.Now:HH:mm:ss}] {step}{(detail.Length > 0 ? $": {detail}" : "")}");
}
