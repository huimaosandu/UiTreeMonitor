// 监控任务配置：七要素模型
using System;
using System.Collections.Generic;
using UiTreeMonitor.Core.Filters;
using UiTreeMonitor.Core.Models;

namespace UiTreeMonitor.Core.Tasks;

/// <summary>监控目标的定位方式。</summary>
public enum TargetKind
{
    /// <summary>按进程名（自动取主窗口）</summary>
    Process,
    /// <summary>按窗口句柄（十进制或 0x 十六进制）</summary>
    WindowHandle,
    /// <summary>按窗口标题（模糊匹配）</summary>
    WindowTitle,
}

/// <summary>监控目标。</summary>
public sealed class MonitorTarget
{
    public TargetKind Kind { get; set; } = TargetKind.Process;
    public string Value { get; set; } = "";   // 进程名 / 句柄字符串 / 标题关键字
    public string? ElementName { get; set; }  // 可选：树内目标节点 Name（限定监控范围）
    public string? ElementAutomationId { get; set; }
    public string? ElementControlType { get; set; }

    public override string ToString() => $"{Kind}:{Value}";
}

/// <summary>监控任务完整配置（七要素）。</summary>
public sealed class MonitorTaskConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    // 要素1：目标
    public MonitorTarget Target { get; set; } = new();

    // 要素2：属性
    public IReadOnlyList<string> Properties { get; set; } = new[] { UiProperties.Name, UiProperties.Value };

    // 要素3：过滤
    public FilterSet Filter { get; set; } = new();

    /// <summary>值提取规则（每条：标记对如 "@@"，或正则 "re:(\d+\.?\d*)"）。
    /// 空列表 = 不提取，值1=原始值；N 条规则 → 采样记录展开为 值1..值N。</summary>
    public IReadOnlyList<string> Extractors { get; set; } = Array.Empty<string>();

    // 要素4：触发器
    public ITriggerSpec Trigger { get; set; } = new ImmediateTriggerSpec();

    // 要素5：定时器（采集间隔）
    public TimerSpec Timer { get; set; } = new() { IntervalMs = 1000 };

    // 要素6：时间器（任务生命周期）
    public TimeBoxSpec TimeBox { get; set; } = new();

    // 要素7：结束条件
    public IStopConditionSpec StopCondition { get; set; } = new NeverStopSpec();

    // 导出
    public string OutputDirectory { get; set; } = "";
    public bool AutoExportOnStop { get; set; } = true;
}

/// <summary>一次采样记录。</summary>
public sealed record SampleRecord(
    DateTime Timestamp,
    int NodeId,
    string NodePath,
    string Property,
    string Value);
