// 录制任务模型：树形步骤（可嵌套循环/触发器容器）+ 任务 JSON 持久化
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UiTreeMonitor.Core.Models;

/// <summary>步骤节点类型。</summary>
public static class StepKinds
{
    public const string Action = "action";     // 一条录制的 ActionRecord
    public const string Loop = "loop";         // 循环体（子步骤重复 N 次）
    public const string Delay = "delay";       // 固定延时
    public const string Timer = "timer";       // 定时触发：等到指定时刻再继续
    public const string Trigger = "trigger";   // 元素监控触发：条件满足后执行子步骤（无子步骤=纯等待点）
    public const string Screenshot = "screenshot"; // 元素截图：按 spec 定位目标元素并对其区域截图保存
}

/// <summary>通用元素监控触发器配置（录制任务触发步骤）。</summary>
public sealed class ElementTriggerSpec
{
    /// <summary>目标元素定位（spec:v1|… 或 coord:v1|x,y，与录制记录同格式）。</summary>
    public string ElementSpec { get; set; } = "";
    /// <summary>元素显示名（配置时快照，仅展示用）。</summary>
    public string ElementName { get; set; } = "";
    /// <summary>监控属性：Name / Value / RangeValue / ToggleState / ExpandCollapseState / IsEnabled</summary>
    public string Property { get; set; } = "Value";
    /// <summary>条件：Equals / NotEquals / Contains / Changed / Greater / Less</summary>
    public string Condition { get; set; } = "Equals";
    /// <summary>期望值（Changed 条件忽略）。</summary>
    public string? ExpectedValue { get; set; }
    /// <summary>超时秒数（0=无限等待；超时该步骤失败）。</summary>
    public int TimeoutSec { get; set; } = 60;
    /// <summary>轮询间隔毫秒。</summary>
    public int PollIntervalMs { get; set; } = 300;
}

/// <summary>树形步骤节点（loop/trigger 可含子步骤=多层嵌套）。</summary>
public sealed class StepNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = StepKinds.Action;
    public bool IsEnabled { get; set; } = true;

    public ActionRecord? Action { get; set; }            // Kind=action
    public int LoopCount { get; set; } = 2;              // Kind=loop
    public int LoopIntervalMs { get; set; } = 500;       // Kind=loop 每轮间隔
    public int DelayMs { get; set; } = 1000;             // Kind=delay
    public DateTime TriggerAt { get; set; } = DateTime.Now.AddHours(1); // Kind=timer
    public ElementTriggerSpec? Trigger { get; set; }     // Kind=trigger
    public string ScreenshotSpec { get; set; } = "";     // Kind=screenshot 目标元素定位（spec:v1|…）
    public string ScreenshotName { get; set; } = "";     // Kind=screenshot 目标元素显示名（配置时快照）
    public string ScreenshotDir { get; set; } = "";      // Kind=screenshot 保存目录（img-时间戳.png）
    public List<StepNode> Children { get; set; } = new(); // loop/trigger 子步骤

    /// <summary>列表显示主文本（TreeView 行）。</summary>
    [JsonIgnore]
    public string DisplayText => Kind switch
    {
        StepKinds.Loop => $"🔁 循环 ×{LoopCount}" + (LoopIntervalMs > 0 ? $"（每轮间隔 {LoopIntervalMs}ms）" : ""),
        StepKinds.Delay => $"⏱ 延时 {DelayMs}ms",
        StepKinds.Timer => $"⏰ 定时 {TriggerAt:yyyy-MM-dd HH:mm:ss}",
        StepKinds.Trigger => Trigger is null
            ? "⚗ 触发器（未配置）"
            : $"⚗ 触发器：{Trigger.ElementName} 的 {Trigger.Property} {CondText(Trigger.Condition)} {(Trigger.Condition == "Changed" ? "" : Trigger.ExpectedValue ?? "")}",
        StepKinds.Screenshot => string.IsNullOrEmpty(ScreenshotSpec)
            ? "📷 截图（未配置）"
            : $"📷 截图 {ScreenshotName}",
        _ => Action is null ? "(空动作)" : $"{Action.PatternName}.{Action.MethodName} {Action.ElementName}",
    };

    /// <summary>次文本（结果/参数摘要）。</summary>
    [JsonIgnore]
    public string DetailText => Kind == StepKinds.Action
        ? Action?.Result ?? (Action?.Parameters is { Length: > 0 } ? string.Join(", ", Action.Parameters) : "")
        : Kind == StepKinds.Trigger && Trigger is not null
            ? $"超时 {Trigger.TimeoutSec}s" + (Children.Count > 0 ? $"，子步骤 {Children.Count} 个" : "（纯等待点）")
            : Kind == StepKinds.Loop ? $"子步骤 {Children.Count} 个"
            : Kind == StepKinds.Screenshot ? $"保存于 {ScreenshotDir}" : "";

    private static string CondText(string c) => c switch
    {
        "Equals" => "=", "NotEquals" => "≠", "Contains" => "包含",
        "Changed" => "发生变化", "Greater" => ">", "Less" => "<", _ => c,
    };
}

/// <summary>录制任务（树形步骤脚本，JSON 落盘于 exe 目录 RecordingTasks\）。</summary>
public sealed class RecordingTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "未命名任务";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    /// <summary>录制来源模式（"uia"=UIA 语义 / "input"=鼠标键盘），仅提示用。</summary>
    public string Mode { get; set; } = "uia";
    public List<StepNode> Steps { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
    public static RecordingTask? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<RecordingTask>(json, JsonOpts); }
        catch { return null; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
