// 统一日志：UI 层 / 采集任务 / 同步组协调器写同一份日志文件
using System;
using System.IO;
using System.Threading;

namespace UiTreeMonitor.Core;

/// <summary>
/// 应用日志（UI / TASK-xxx / SYNC-xxxxxx 统一写入）。
/// 目录解析：exe 在工程源码树内（dotnet run，bin/Debug/... 或 bin/Release/...）→ 工程根\logs
/// （源码调试时日志集中在工程里，方便锁定问题）；发布版 exe 独立目录 → exe目录\logs（便携）。
/// 向上查找 UiTreeMonitor.sln 作为工程根标记；进程生命周期内目录只解析一次（Lazy）。
/// 文件命名：本次启动时刻（yyyyMMdd_HHmmss.log）——每次打开软件都新建一份日志，
/// 进程生命周期内固定写这一份（源码运行与打包版行为一致；worker 独立进程另起自己的文件）。
/// </summary>
public static class AppLog
{
    private static readonly Lazy<string> LogDirLazy = new(ResolveLogDir);

    private static readonly Lazy<string> LogFileLazy = new(
        () => Path.Combine(LogDir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log"));

    // 写入串行门闩：AppendAllText 以 FileShare.Read 打开，并发写同一路径会 sharing violation，
    // 导致降级时竞争备用文件（一方被挤到 _2）。进程内串行化写入，降级目标才是确定的。
    private static readonly object WriteGate = new();

    public static string LogDir => LogDirLazy.Value;

    /// <summary>本次运行的日志文件路径（启动时刻命名，每次启动新建一份）。</summary>
    public static string LogFilePath => LogFileLazy.Value;

    /// <summary>
    /// 写一行日志（tag 如 UI / TASK-任务名 / SYNC-组号）；任何失败静默（不影响采集）。
    /// 日志文件被删除（句柄未释放导致 delete-pending）或被占用时，降级写备用文件（启动文件名_1.log 起）继续保存，
    /// 主文件恢复可写后自动回归（每次先尝试主文件）。
    /// </summary>
    public static void Write(string tag, string message)
    {
        lock (WriteGate)
        {
            try
            {
                var line = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}{Environment.NewLine}";
                Directory.CreateDirectory(LogDir);
                try { File.AppendAllText(LogFilePath, line); return; }
                catch
                {
                    // 主文件刚被删除且并发写入句柄尚未释放（delete-pending，毫秒级窗口）时，
                    // 立即降级会把本行误写进备用文件——先短重试主文件，仍不可写再降级。
                    for (int r = 0; r < 3; r++)
                    {
                        Thread.Sleep(5);
                        try { File.AppendAllText(LogFilePath, line); return; }
                        catch { }
                    }
                }
                var baseName = Path.GetFileNameWithoutExtension(LogFilePath);
                for (int i = 1; i <= 9; i++)
                {
                    try { File.AppendAllText(Path.Combine(LogDir, $"{baseName}_{i}.log"), line); return; }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static string ResolveLogDir()
    {
        try
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent!)
            {
                if (File.Exists(Path.Combine(dir.FullName, "UiTreeMonitor.sln")))
                    return Path.Combine(dir.FullName, "logs");
            }
        }
        catch { /* 解析异常退回 exe 目录 */ }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
    }
}
