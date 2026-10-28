// 结束条件：规格 + 运行时判断
using System;
using System.Collections.Generic;

namespace UiTreeMonitor.Core.Tasks;

/// <summary>结束条件规格。</summary>
public interface IStopConditionSpec { string Kind => GetType().Name; }

/// <summary>运行固定时长后停止（时间器到期等价触发此条件）。</summary>
public sealed class DurationStopSpec : IStopConditionSpec
{
    public double Seconds { get; set; } = 60;
}

/// <summary>采集满 N 次后停止。</summary>
public sealed class SampleCountStopSpec : IStopConditionSpec
{
    public int Count { get; set; } = 100;
}

/// <summary>某属性值达到阈值/正则匹配后停止。</summary>
public sealed class ValueStopSpec : IStopConditionSpec
{
    public string Property { get; set; } = "Value";
    public double? Threshold { get; set; }        // 数值阈值
    public ComparisonOp Op { get; set; } = ComparisonOp.GreaterOrEqual;
    public string? RegexPattern { get; set; }     // 正则匹配（与阈值二选一）
}

/// <summary>值持续变化/持续不变 N 秒后停止。</summary>
public sealed class ChangeStopSpec : IStopConditionSpec
{
    public string Property { get; set; } = "Value";
    public bool StopOnChange { get; set; } = true;   // true=值变化即停；false=值不变 N 秒即停
    public double Seconds { get; set; } = 10;
}

/// <summary>不自动结束。</summary>
public sealed class NeverStopSpec : IStopConditionSpec { }

/// <summary>结束条件运行时。</summary>
public interface IStopConditionRuntime
{
    /// <summary>传入最新采样上下文，返回 true 表示应停止。</summary>
    bool ShouldStop(StopContext ctx);
}

/// <summary>结束条件判断上下文。</summary>
public sealed class StopContext
{
    public DateTime Now { get; set; } = DateTime.Now;
    public DateTime TaskStartedAt { get; set; }
    public int SampleCount { get; set; }
    public string? LastValue { get; set; }                  // 最新采样值（首个监控属性）
    public IReadOnlyDictionary<string, string?> LastValues { get; set; } = new Dictionary<string, string?>();
    public DateTime? LastValueChangedAt { get; set; }
    public string? PreviousValue { get; set; }
}

public static class StopConditionRuntimeFactory
{
    public static IStopConditionRuntime Create(IStopConditionSpec spec) => spec switch
    {
        DurationStopSpec s => new DurationStopRuntime(s),
        SampleCountStopSpec s => new SampleCountStopRuntime(s),
        ValueStopSpec s => new ValueStopRuntime(s),
        ChangeStopSpec s => new ChangeStopRuntime(s),
        NeverStopSpec => new NeverStopRuntime(),
        _ => new NeverStopRuntime(),
    };
}

public sealed class DurationStopRuntime : IStopConditionRuntime
{
    private readonly DurationStopSpec _s;
    public DurationStopRuntime(DurationStopSpec s) { _s = s; }
    public bool ShouldStop(StopContext ctx) => ctx.Now - ctx.TaskStartedAt >= TimeSpan.FromSeconds(_s.Seconds);
}

public sealed class SampleCountStopRuntime : IStopConditionRuntime
{
    private readonly SampleCountStopSpec _s;
    public SampleCountStopRuntime(SampleCountStopSpec s) { _s = s; }
    public bool ShouldStop(StopContext ctx) => ctx.SampleCount >= _s.Count;
}

public sealed class ValueStopRuntime : IStopConditionRuntime
{
    private readonly ValueStopSpec _s;
    public ValueStopRuntime(ValueStopSpec s) { _s = s; }

    public bool ShouldStop(StopContext ctx)
    {
        if (!ctx.LastValues.TryGetValue(_s.Property, out var raw) || raw is null) return false;

        if (_s.RegexPattern is not null)
            return System.Text.RegularExpressions.Regex.IsMatch(raw, _s.RegexPattern,
                System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));

        if (_s.Threshold is double t && double.TryParse(raw, out var val))
            return _s.Op switch
            {
                ComparisonOp.Greater => val > t,
                ComparisonOp.GreaterOrEqual => val >= t,
                ComparisonOp.Less => val < t,
                ComparisonOp.LessOrEqual => val <= t,
                ComparisonOp.Equal => Math.Abs(val - t) < 1e-9,
                ComparisonOp.NotEqual => Math.Abs(val - t) >= 1e-9,
                _ => false,
            };
        return false;
    }
}

public sealed class ChangeStopRuntime : IStopConditionRuntime
{
    private readonly ChangeStopSpec _s;
    public ChangeStopRuntime(ChangeStopSpec s) { _s = s; }

    public bool ShouldStop(StopContext ctx)
    {
        var cur = ctx.LastValues.TryGetValue(_s.Property, out var v) ? v : null;
        var prev = ctx.PreviousValue;
        bool changed = cur != prev;
        if (changed) ctx.LastValueChangedAt = ctx.Now;

        if (_s.StopOnChange)
            return changed;
        else
            return ctx.LastValueChangedAt is DateTime t && ctx.Now - t >= TimeSpan.FromSeconds(_s.Seconds);
    }
}

public sealed class NeverStopRuntime : IStopConditionRuntime
{
    public bool ShouldStop(StopContext ctx) => false;
}
