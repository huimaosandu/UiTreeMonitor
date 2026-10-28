// 持久化：任务配置 JSON 读写 + 采样历史 CSV 落盘
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UiTreeMonitor.Core.Tasks;

namespace UiTreeMonitor.Persistence;

/// <summary>任务配置持久化（JSON）。</summary>
public sealed class TaskConfigStore
{
    private readonly string _directory;

    public TaskConfigStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public string Save(MonitorTaskConfig config)
    {
        var path = Path.Combine(_directory, $"{config.Name}_{config.Id:N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
        return path;
    }

    public IReadOnlyList<MonitorTaskConfig> LoadAll()
    {
        var result = new List<MonitorTaskConfig>();
        if (!Directory.Exists(_directory)) return result;
        foreach (var f in Directory.GetFiles(_directory, "*.json"))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<MonitorTaskConfig>(File.ReadAllText(f), Options);
                if (cfg is not null) result.Add(cfg);
            }
            catch { /* 跳过损坏配置 */ }
        }
        return result;
    }

    public void Delete(Guid id)
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var f in Directory.GetFiles(_directory, $"*_{id:N}.json"))
            File.Delete(f);
    }
}

/// <summary>采样历史落盘（CSV，UTF-8 with BOM，Excel 可直接打开）。</summary>
public sealed class SampleCsvWriter
{
    private readonly string _directory;
    private readonly object _lock = new();

    public SampleCsvWriter(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    /// <summary>把采样记录追加写入 {任务名}_{yyyyMMdd}.csv。</summary>
    public string Append(string taskName, Guid taskId, IEnumerable<SampleRecord> records)
    {
        lock (_lock)
        {
            var file = Path.Combine(_directory, $"{Sanitize(taskName)}_{DateTime.Now:yyyyMMdd}.csv");
            bool exists = File.Exists(file);
            var sb = new System.Text.StringBuilder();
            if (!exists)
                sb.AppendLine("时间戳,节点ID,节点路径,属性,值");

            foreach (var r in records)
                sb.AppendLine($"{r.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{r.NodeId},{CsvEscape(r.NodePath)},{CsvEscape(r.Property)},{CsvEscape(r.Value)}");

            // UTF-8 with BOM：Excel/记事本可直接识别
            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(
                System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            using var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
            if (exists) fs.Write(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
            else fs.Write(bytes);
            return file;
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string CsvEscape(string? s)
    {
        if (s is null) return "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}
