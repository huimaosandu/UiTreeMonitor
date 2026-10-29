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
