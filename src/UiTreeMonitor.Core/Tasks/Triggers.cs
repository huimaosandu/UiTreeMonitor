// 触发器：规格定义（配置数据，可序列化）+ 运行时判断
using System;

namespace UiTreeMonitor.Core.Tasks;

/// <summary>触发器规格（配置数据）。</summary>
public interface ITriggerSpec { string Kind => GetType().Name; }

/// <summary>立刻触发：任务注册即采集。</summary>
public sealed class ImmediateTriggerSpec : ITriggerSpec { }

/// <summary>定时触发：到指定时刻 / 每隔 N 秒触发一次。</summary>
public sealed class ScheduleTriggerSpec : ITriggerSpec
{
    public DateTime? FirstFireAt { get; set; }      // 为空=立即算起点
    public int RepeatIntervalMs { get; set; } = 1000;
    public bool Repeat { get; set; } = true;
}

/// <summary>值变化触发：监控属性与上次不同时触发。</summary>
public sealed class ValueChangeTriggerSpec : ITriggerSpec
{
    public string Property { get; set; } = "Value";
}

/// <summary>阈值触发：数值属性达到阈值时触发。</summary>
public sealed class ThresholdTriggerSpec : ITriggerSpec
{
    public string Property { get; set; } = "Value";
    public double Threshold { get; set; }
    public ComparisonOp Op { get; set; } = ComparisonOp.GreaterOrEqual;
}

/// <summary>他树变化触发：另一监控任务的值变化时触发本任务（跨树联动）。</summary>
public sealed class CrossTreeTriggerSpec : ITriggerSpec
{
    public Guid OtherTaskId { get; set; }
    public string OtherProperty { get; set; } = "Value";
}

/// <summary>组合触发器（AND/OR）。</summary>
public sealed class CompositeTriggerSpec : ITriggerSpec
{
    public IReadOnlyList<ITriggerSpec> Children { get; set; } = Array.Empty<ITriggerSpec>();
    public bool IsAnd { get; set; } = true;
}

public enum ComparisonOp { Greater, GreaterOrEqual, Less, LessOrEqual, Equal, NotEqual }

/// <summary>触发器运行时：判断"此刻是否应采集一次"。</summary>
public interface ITriggerRuntime
{
    /// <summary>返回 true = 应触发一次采集。调用方在采集后调用 Consume() 复位单次触发。</summary>
    bool ShouldFire(TriggerContext ctx);
}

/// <summary>触发器判断上下文。</summary>
public sealed class TriggerContext
{
    public DateTime Now { get; set; } = DateTime.Now;
    public DateTime TaskStartedAt { get; set; }
    public DateTime? LastFireAt { get; set; }
    /// <summary>上一次采样的属性值（用于值变化/阈值判断）。</summary>
    public IReadOnlyDictionary<string, string?> LastValues { get; set; } = new System.Collections.Generic.Dictionary<string, string?>();
    /// <summary>跨树引用：其他任务最近一次采样值。</summary>
    public Func<Guid, string, string?>? CrossTreeValue { get; set; }
}

/// <summary>触发器运行时工厂：按规格创建运行时。</summary>
public static class TriggerRuntimeFactory
{
    public static ITriggerRuntime Create(ITriggerSpec spec) => spec switch
    {
        ImmediateTriggerSpec => new ImmediateTriggerRuntime(),
        ScheduleTriggerSpec s => new ScheduleTriggerRuntime(s),
        ValueChangeTriggerSpec s => new ValueChangeTriggerRuntime(s),
        ThresholdTriggerSpec s => new ThresholdTriggerRuntime(s),
        CrossTreeTriggerSpec s => new CrossTreeTriggerRuntime(s),
        CompositeTriggerSpec s => new CompositeTriggerRuntime(s),
        _ => new ImmediateTriggerRuntime(),
    };
}

public sealed class ImmediateTriggerRuntime : ITriggerRuntime
{
    private bool _fired;
    public bool ShouldFire(TriggerContext ctx)
    {
        if (!_fired) { _fired = true; return true; }
        return false;
    }
}

public sealed class ScheduleTriggerRuntime : ITriggerRuntime
{
    private readonly ScheduleTriggerSpec _s;
    private DateTime? _next;
    public ScheduleTriggerRuntime(ScheduleTriggerSpec s) { _s = s; }

    public bool ShouldFire(TriggerContext ctx)
    {
        _next ??= _s.FirstFireAt ?? ctx.TaskStartedAt;
        if (ctx.Now >= _next.Value)
        {
            if (_s.Repeat) _next = _next.Value.AddMilliseconds(_s.RepeatIntervalMs);
            else _next = null;
            return true;
        }
        return false;
    }
}

public sealed class ValueChangeTriggerRuntime : ITriggerRuntime
{
    private readonly ValueChangeTriggerSpec _s;
    public ValueChangeTriggerRuntime(ValueChangeTriggerSpec s) { _s = s; }

    public bool ShouldFire(TriggerContext ctx)
    {
        if (ctx.LastFireAt is null) return true; // 首次也触发
        var last = ctx.LastValues.TryGetValue(_s.Property, out var v) ? v : null;
        var cur = ctx.LastValues.TryGetValue(_s.Property, out var c) ? c : null;
        return last != cur;
    }
}

public sealed class ThresholdTriggerRuntime : ITriggerRuntime
{
    private readonly ThresholdTriggerSpec _s;
    public ThresholdTriggerRuntime(ThresholdTriggerSpec s) { _s = s; }

    public bool ShouldFire(TriggerContext ctx)
    {
        if (!ctx.LastValues.TryGetValue(_s.Property, out var raw) || !double.TryParse(raw, out var val))
            return false;
        return _s.Op switch
        {
            ComparisonOp.Greater => val > _s.Threshold,
            ComparisonOp.GreaterOrEqual => val >= _s.Threshold,
            ComparisonOp.Less => val < _s.Threshold,
            ComparisonOp.LessOrEqual => val <= _s.Threshold,
            ComparisonOp.Equal => Math.Abs(val - _s.Threshold) < 1e-9,
            ComparisonOp.NotEqual => Math.Abs(val - _s.Threshold) >= 1e-9,
            _ => false,
        };
    }
}

public sealed class CrossTreeTriggerRuntime : ITriggerRuntime
{
    private readonly CrossTreeTriggerSpec _s;
    private string? _lastSeen;
    public CrossTreeTriggerRuntime(CrossTreeTriggerSpec s) { _s = s; }

    public bool ShouldFire(TriggerContext ctx)
    {
        var cur = ctx.CrossTreeValue?.Invoke(_s.OtherTaskId, _s.OtherProperty);
        if (cur is null) return false;
        bool changed = cur != _lastSeen;
        _lastSeen = cur;
        return changed;
    }
}

public sealed class CompositeTriggerRuntime : ITriggerRuntime
{
    private readonly CompositeTriggerSpec _s;
    private readonly ITriggerRuntime[] _children;
    public CompositeTriggerRuntime(CompositeTriggerSpec s)
    {
        _s = s;
        _children = new ITriggerRuntime[s.Children.Count];
        for (int i = 0; i < s.Children.Count; i++)
            _children[i] = TriggerRuntimeFactory.Create(s.Children[i]);
    }

    public bool ShouldFire(TriggerContext ctx)
    {
        if (_children.Length == 0) return false;
        return _s.IsAnd
            ? Array.TrueForAll(_children, c => c.ShouldFire(ctx))
            : Array.Exists(_children, c => c.ShouldFire(ctx));
    }
}
