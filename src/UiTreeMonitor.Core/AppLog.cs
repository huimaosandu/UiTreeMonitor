// 统一日志：UI 层 / 采集任务 / 同步组协调器写同一份日志文件
using System;
using System.IO;

namespace UiTreeMonitor.Core;

/// <summary>
/// 应用日志（UI / TASK-xxx / SYNC-xxxxxx 统一写入）。
/// 目录解析：exe 在工程源码树内（dotnet run，bin/Debug/... 或 bin/Release/...）→ 工程根\logs
/// （源码调试时日志集中在工程里，方便锁定问题）；发布版 exe 独立目录 → exe目录\logs（便携）。
/// 向上查找 UiTreeMonitor.sln 作为工程根标记；进程生命周期内目录只解析一次（Lazy）。
/// </summary>
public static class AppLog
{
    private static readonly Lazy<string> LogDirLazy = new(ResolveLogDir);

    public static string LogDir => LogDirLazy.Value;

    public static string LogFilePath => Path.Combine(LogDir, DateTime.Now.ToString("yyyyMMdd") + ".log");

    /// <summary>写一行日志（tag 如 UI / TASK-任务名 / SYNC-组号）；任何失败静默（不影响采集）。</summary>
    public static void Write(string tag, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(LogFilePath, $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}{Environment.NewLine}");
        }
        catch { }
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
