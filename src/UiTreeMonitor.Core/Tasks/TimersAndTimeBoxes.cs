// 定时器（采集间隔）与时间器（任务生命周期）
using System;

namespace UiTreeMonitor.Core.Tasks;

/// <summary>定时器规格：多久采集一次。</summary>
public sealed class TimerSpec
{
    public int IntervalMs { get; set; } = 1000;
}

/// <summary>时间器规格：任务总共跑多久 / 起止窗口。</summary>
public interface ITimeBoxSpec { string Kind => GetType().Name; }

/// <summary>总时长：从任务启动算起运行 N 秒后到期。</summary>
public sealed class DurationTimeBoxSpec : ITimeBoxSpec
{
    public double Seconds { get; set; } = 60;
}

/// <summary>到指定时刻停止。</summary>
public sealed class EndAtTimeBoxSpec : ITimeBoxSpec
{
    public DateTime EndAt { get; set; }
}

/// <summary>起止窗口：仅在窗口内运行。</summary>
public sealed class WindowTimeBoxSpec : ITimeBoxSpec
{
    public TimeSpan StartTime { get; set; }   // 每日开始时刻
    public TimeSpan EndTime { get; set; }     // 每日结束时刻
}

/// <summary>周期巡检：跑 N 秒 → 停 M 秒 → 再跑 N 秒。</summary>
public sealed class LoopTimeBoxSpec : ITimeBoxSpec
{
    public double RunSeconds { get; set; } = 30;
    public double PauseSeconds { get; set; } = 300;
}

/// <summary>无时间器：不限制生命周期。</summary>
public sealed class NoTimeBoxSpec : ITimeBoxSpec { }

/// <summary>时间器规格集合（同时生效，任一到期即结束）。</summary>
public sealed class TimeBoxSpec
{
    public IReadOnlyList<ITimeBoxSpec> Boxes { get; set; } = Array.Empty<ITimeBoxSpec>();
    public bool IsEmpty => Boxes.Count == 0;
}

/// <summary>时间器运行时状态。</summary>
public sealed class TimeBoxRuntime
{
    private readonly TimeBoxSpec _spec;
    private readonly LoopTimeBoxSpec? _loop;
    private readonly DateTime _startedAt;
    private DateTime _loopRunEnd;    // 当前运行段结束时刻
    private DateTime _loopPauseEnd;  // 当前暂停段结束时刻
    private bool _inPause;

    public TimeBoxRuntime(TimeBoxSpec spec)
    {
        _spec = spec;
        _startedAt = DateTime.Now;
        _loop = null;
        foreach (var b in _spec.Boxes)
            if (b is LoopTimeBoxSpec l) { _loop = l; break; }
        _loopRunEnd = _startedAt.AddSeconds(_loop?.RunSeconds ?? 0);
        _loopPauseEnd = _startedAt;
        _inPause = false;
    }

    /// <summary>是否已到期（应结束任务）。</summary>
    public bool IsExpired(DateTime now)
    {
        foreach (var b in _spec.Boxes)
        {
            switch (b)
            {
                case DurationTimeBoxSpec d:
                    if (now - _startedAt >= TimeSpan.FromSeconds(d.Seconds)) return true;
                    break;
                case EndAtTimeBoxSpec e:
                    if (now >= e.EndAt) return true;
                    break;
                case WindowTimeBoxSpec w:
                    var tod = now.TimeOfDay;
                    if (w.StartTime <= w.EndTime)
                    { if (tod < w.StartTime || tod > w.EndTime) return true; }
                    else // 跨午夜窗口
                    { if (tod < w.StartTime && tod > w.EndTime) return true; }
                    break;
                case LoopTimeBoxSpec:
                    // 运行段已结束 → 到期（整个任务结束）；暂停段由 AdvanceLoop 处理恢复
                    if (!_inPause && now >= _loopRunEnd) return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>推进 Loop 状态机：运行段结束 → 进入暂停 → 暂停结束 → 回到运行。</summary>
    public void AdvanceLoop(DateTime now)
    {
        if (_loop is null) return;

        if (!_inPause && now >= _loopRunEnd)
        {
            _inPause = true;
            _loopPauseEnd = now.AddSeconds(_loop.PauseSeconds);
        }
        else if (_inPause && now >= _loopPauseEnd)
        {
            _inPause = false;
            _loopRunEnd = now.AddSeconds(_loop.RunSeconds);
        }
    }

    /// <summary>当前是否处于暂停段（暂停时不采集）。</summary>
    public bool IsInPause => _inPause;
}

