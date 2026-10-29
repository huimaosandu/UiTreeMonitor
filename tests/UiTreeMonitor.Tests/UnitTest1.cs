using System;
using System.Collections.Generic;
using UiTreeMonitor.Core.Filters;
using UiTreeMonitor.Core.Models;
using UiTreeMonitor.Core.Tasks;
using Xunit;

namespace UiTreeMonitor.Tests;

public class FilterTests
{
    private static ElementNode MakeNode(int id, string name, string ctrlType = "Text")
        => new()
        {
            Id = id, ParentId = 0, Name = name, ControlType = ctrlType,
            AutomationId = "", ClassName = "", ProcessId = 123,
        };

    private static TreeSnapshot TreeWith(params ElementNode[] nodes)
    {
        var dict = new Dictionary<int, ElementNode>();
        foreach (var n in nodes) dict[n.Id] = n;
        return new TreeSnapshot { RootId = nodes.Length > 0 ? nodes[0].Id : 0, Nodes = dict };
    }

    [Fact]
    public void FilterTree_SimpleMatch_ReturnsMatched()
    {
        var node = MakeNode(1, "温度计");
        var tree = TreeWith(node);

        var rule = new FilterRule { Property = "Name", Pattern = "温度" };
        var set = new FilterSet { Rules = new[] { rule }, IsAnd = true };

        var matches = set.FilterTree(tree);
        Assert.Contains(1, matches);
    }

    [Fact]
    public void FilterTree_RegexWithGroup_ExtractsValue()
    {
        var node = MakeNode(1, "温度计");
        node = node with { Value = "25.3" };
        var tree = TreeWith(node);

        var rule = new FilterRule { Property = "Name", Pattern = "温度(?<值>\\d+)", IsExtract = true, ExtractGroup = "值" };
        var set = new FilterSet { Rules = new[] { rule }, IsAnd = true };

        var matches = set.FilterTree(tree);
        Assert.Contains(1, matches);
    }

    [Fact]
    public void FilterTree_NoMatch_ReturnsEmpty()
    {
        var node = MakeNode(1, "按钮A", "Button");
        var tree = TreeWith(node);

        var rule = new FilterRule { Property = "Name", Pattern = "温度" };
        var set = new FilterSet { Rules = new[] { rule }, IsAnd = true };

        Assert.Empty(set.FilterTree(tree));
    }
}

public class TriggerTests
{
    [Fact]
    public void Immediate_AlwaysFires()
    {
        var trigger = TriggerRuntimeFactory.Create(new ImmediateTriggerSpec());
        var ctx = new TriggerContext { Now = DateTime.Now, LastValues = new Dictionary<string, string?>() };
        Assert.True(trigger.ShouldFire(ctx));
    }

    [Fact]
    public void Schedule_FirstFire_Immediate()
    {
        var trigger = TriggerRuntimeFactory.Create(new ScheduleTriggerSpec { RepeatIntervalMs = 1000 });
        var ctx = new TriggerContext { Now = DateTime.Now, LastValues = new Dictionary<string, string?>() };
        Assert.True(trigger.ShouldFire(ctx)); // 首次立即
    }

    [Fact]
    public void ValueChange_NoLastValue_Fires()
    {
        var trigger = TriggerRuntimeFactory.Create(new ValueChangeTriggerSpec { Property = "Value" });
        var ctx = new TriggerContext { Now = DateTime.Now, LastValues = new Dictionary<string, string?>() };
        Assert.True(trigger.ShouldFire(ctx)); // 首次无基线 → 触发
    }

    [Fact]
    public void Threshold_Reached_Fires()
    {
        var trigger = TriggerRuntimeFactory.Create(
            new ThresholdTriggerSpec { Property = "Value", Threshold = 100, Op = ComparisonOp.GreaterOrEqual });
        var ctx = new TriggerContext
        {
            Now = DateTime.Now,
            LastValues = new Dictionary<string, string?> { ["Value"] = "150" },
        };
        Assert.True(trigger.ShouldFire(ctx));
    }

    [Fact]
    public void Threshold_NotReached_DoesNotFire()
    {
        var trigger = TriggerRuntimeFactory.Create(
            new ThresholdTriggerSpec { Property = "Value", Threshold = 100, Op = ComparisonOp.GreaterOrEqual });
        var ctx = new TriggerContext
        {
            Now = DateTime.Now,
            LastValues = new Dictionary<string, string?> { ["Value"] = "50" },
        };
        Assert.False(trigger.ShouldFire(ctx));
    }
}

public class TimeBoxTests
{
    [Fact]
    public void Duration_NotExpired()
    {
        var spec = new TimeBoxSpec { Boxes = new[] { new DurationTimeBoxSpec { Seconds = 60 } } };
        var rt = new TimeBoxRuntime(spec);
        Assert.False(rt.IsExpired(DateTime.Now.AddSeconds(10)));
    }

    [Fact]
    public void Duration_Expired_AfterElapsed()
    {
        var spec = new TimeBoxSpec { Boxes = new[] { new DurationTimeBoxSpec { Seconds = 2 } } };
        var rt = new TimeBoxRuntime(spec);
        Assert.True(rt.IsExpired(DateTime.Now.AddSeconds(5)));
    }
}

public class TargetResolverTests
{
    [Fact]
    public void Resolve_InvalidHandle_ReturnsNull()
    {
        var target = new MonitorTarget { Kind = TargetKind.WindowHandle, Value = "0xDEADBEEF" };
        var result = TargetResolver.Resolve(target);
        Assert.Null(result);
    }
}

public class ElementNodeTests
{
    private static ElementNode NodeWithPattern(params (string Pat, string Prop, string Val)[] entries)
    {
        var pats = new List<PatternData>();
        foreach (var g in entries.GroupBy(e => e.Pat))
            pats.Add(new PatternData(g.Key, g.First().Val,
                g.Select(e => new PatternPropData(e.Prop, e.Val)).ToList()));
        return new() { Id = 1, PatternDataList = pats };
    }

    [Fact]
    public void GetProperty_DottedPatternPath_ReturnsValue()
    {
        var node = NodeWithPattern(("ValuePattern", "Value", "hello"));
        Assert.Equal("hello", node.GetProperty("ValuePattern.Value"));
    }

    [Fact]
    public void GetProperty_DottedPath_Unknown_ReturnsNull()
    {
        var node = NodeWithPattern(("ValuePattern", "Value", "hello"));
        Assert.Null(node.GetProperty("TogglePattern.ToggleState"));
    }

    [Fact]
    public void GetProperty_PlainName_StillWorks()
    {
        var node = new ElementNode { Id = 1, Name = "温度计", ControlType = "Text" };
        Assert.Equal("温度计", node.GetProperty("Name"));
        Assert.Equal("Text", node.GetProperty("ControlType"));
    }
}

public class ValueExtractorTests
{
    [Fact]
    public void MarkerPair_ExtractsBetweenMarkers()
    {
        // 用户场景：Value = "@@614.466@@" → 截取 614.466
        Assert.Equal("614.466", UiTreeMonitor.Core.Filters.ValueExtractor.ExtractOne("@@614.466@@", "@@"));
    }

    [Fact]
    public void MarkerPair_NonGreedy_StopsAtFirstClose()
    {
        Assert.Equal("A", UiTreeMonitor.Core.Filters.ValueExtractor.ExtractOne("@@A@@ tail @@B@@", "@@"));
    }

    [Fact]
    public void NoRules_ReturnsRawValue()
    {
        var result = UiTreeMonitor.Core.Filters.ValueExtractor.ExtractAll("612.872", Array.Empty<string>());
        Assert.Single(result);
        Assert.Equal("612.872", result[0]);
    }

    [Fact]
    public void RegexRule_TakesFirstCaptureGroup()
    {
        Assert.Equal("614.466",
            UiTreeMonitor.Core.Filters.ValueExtractor.ExtractOne("Value = \"614.466\"", @"re:(\d+\.\d+)"));
    }

    [Fact]
    public void RegexRule_NoMatch_ReturnsEmpty()
    {
        Assert.Equal("", UiTreeMonitor.Core.Filters.ValueExtractor.ExtractOne("abc", @"re:(\d+)"));
    }

    [Fact]
    public void MultipleRules_ProduceOrderedValues()
    {
        var rules = new[] { "@@", @"re:(\d+)" };
        var result = UiTreeMonitor.Core.Filters.ValueExtractor.ExtractAll("x @@Temp: 42@@ y 7", rules);
        Assert.Equal(2, result.Count);
        Assert.Equal("Temp: 42", result[0]); // 值1：标记对
        Assert.Equal("42", result[1]);       // 值2：正则
    }

    [Fact]
    public void InvalidRegex_ReturnsEmpty_NotThrow()
    {
        Assert.Equal("", UiTreeMonitor.Core.Filters.ValueExtractor.ExtractOne("any", "re:(["));
    }
}

public class ScheduleTriggerRhythmTests
{
    // ScheduleTriggerRuntime 首次用 TaskStartedAt 作起点（MonitorTask 同款），测试里必须设置
    private static TriggerContext Ctx(DateTime now) => new() { Now = now, TaskStartedAt = now };

    [Fact]
    public void Schedule_Repeat_FiresImmediatelyThenByInterval()
    {
        var start = new DateTime(2026, 10, 29, 11, 0, 0);
        var trigger = TriggerRuntimeFactory.Create(new ScheduleTriggerSpec { RepeatIntervalMs = 500 });

        Assert.True(trigger.ShouldFire(Ctx(start)));                   // 首采立即
        Assert.False(trigger.ShouldFire(Ctx(start.AddMilliseconds(200)))); // 间隔内不触发
        Assert.True(trigger.ShouldFire(Ctx(start.AddMilliseconds(500))));  // 到期再触发
        Assert.False(trigger.ShouldFire(Ctx(start.AddMilliseconds(700))));
        Assert.True(trigger.ShouldFire(Ctx(start.AddMilliseconds(1000))));
    }

    [Fact]
    public void Immediate_FiresOnlyOnce()
    {
        // 固化行为认知：Immediate 只触发一次——监控任务要用 ScheduleTriggerSpec 才能持续采集
        var trigger = TriggerRuntimeFactory.Create(new ImmediateTriggerSpec());
        var ctx = Ctx(DateTime.Now);
        Assert.True(trigger.ShouldFire(ctx));
        Assert.False(trigger.ShouldFire(ctx));
        Assert.False(trigger.ShouldFire(ctx));
    }
}
