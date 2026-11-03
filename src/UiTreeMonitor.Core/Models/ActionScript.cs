namespace UiTreeMonitor.Core.Models;

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>操作脚本（多条 ActionRecord 组成的序列，支持 JSON 序列化落盘）。</summary>
public sealed class ActionScript
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "未命名脚本";
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public List<ActionRecord> Records { get; init; } = new();

    /// <summary>序列化为 JSON 字符串。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>从 JSON 字符串反序列化。</summary>
    public static ActionScript? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<ActionScript>(json, JsonOptions); }
        catch { return null; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}
