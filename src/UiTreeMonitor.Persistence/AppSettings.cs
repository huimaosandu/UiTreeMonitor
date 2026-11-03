// 全局设置：持久化到 ~/.ui-tree-monitor/settings.json
using System;
using System.IO;
using System.Text.Json;

namespace UiTreeMonitor.Persistence;

/// <summary>应用全局设置。</summary>
public sealed class AppSettings
{
    /// <summary>值提取默认截取标记（起止同串，如 @@）。非法/为空时回落 @@。</summary>
    public string ExtractMarker { get; set; } = "@@";

    // ==== 自动录制捕获选项（默认：捕获鼠标左右键+键盘；UIA 事件关闭）====
    public bool RecordMouseLeft { get; set; } = true;
    public bool RecordMouseRight { get; set; } = true;
    public bool RecordKeyboard { get; set; } = true;
    public bool RecordUiaEvents { get; set; } = false;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>规范截取标记：去空白；为空或含正则风险无法转义时回落默认 @@。</summary>
    public static string NormalizeMarker(string? marker)
    {
        var m = (marker ?? "").Trim();
        return m.Length == 0 ? "@@" : m;
    }

    /// <summary>从 settings.json 读取；不存在或损坏时返回默认值。</summary>
    public static AppSettings Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath), Options);
                if (s is not null)
                {
                    s.ExtractMarker = NormalizeMarker(s.ExtractMarker);
                    return s;
                }
            }
        }
        catch { /* 损坏则用默认 */ }
        return new AppSettings();
    }

    /// <summary>保存到 settings.json。</summary>
    public void Save(string filePath)
    {
        ExtractMarker = NormalizeMarker(ExtractMarker);
        File.WriteAllText(filePath, JsonSerializer.Serialize(this, Options));
    }
}
