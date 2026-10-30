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
    public void CompileMarkedSample_ValueWithQuotes_ExtractsNumber()
    {
        // 用户场景：示例 Value = "@@614.466@@"（标记 @@）→ 锚点正则 → 对实际值提取 614.466
        var rule = UiTreeMonitor.Core.Filters.ValueExtractor.CompileFromMarkedSample(
            "Value = \"@@614.466@@\"", "@@");
        Assert.NotNull(rule);
        Assert.Equal("614.466",
            UiTreeMonitor.Core.Filters.ValueExtractor.ExtractOne("Value = \"614.466\"", rule!));
    }

    [Fact]
    public void CompileMarkedSample_NoSuffix_AnchorsByPrefixOnly()
    {
        // 第二个标记在结尾（后缀为空）：仅前缀锚定，贪婪取前缀后的全部内容
        var rule = UiTreeMonitor.Core.Filters.ValueExtractor.CompileFromMarkedSample("Temp: @@42@@", "@@");
        Assert.NotNull(rule);
        Assert.Equal("99.5", UiTreeMonitor.Core.Filters.ValueExtractor.ExtractOne("Temp: 99.5", rule!));
    }

    [Fact]
    public void CompileMarkedSample_UnpairedMarker_ReturnsNull()
    {
        Assert.Null(UiTreeMonitor.Core.Filters.ValueExtractor.CompileFromMarkedSample("Value = \"@@614.466\"", "@@"));
        Assert.Null(UiTreeMonitor.Core.Filters.ValueExtractor.CompileFromMarkedSample("no marker here", "@@"));
    }

    [Fact]
    public void CompileMarkedSample_CustomMarker_Works()
    {
        // 设置里自定义截取字符（如 ##）：示例 Value = "##614.466##"
        var rule = UiTreeMonitor.Core.Filters.ValueExtractor.CompileFromMarkedSample(
            "Value = \"##614.466##\"", "##");
        Assert.NotNull(rule);
        Assert.Equal("614.466",
            UiTreeMonitor.Core.Filters.ValueExtractor.ExtractOne("Value = \"614.466\"", rule!));
    }

    [Fact]
    public void CompileMarkedSample_SpecialCharsInPrefix_Escaped()
    {
        // 前缀含正则元字符（括号、点、百分号）时必须按字面匹配，对实际值提取其后的数值
        var rule = UiTreeMonitor.Core.Filters.ValueExtractor.CompileFromMarkedSample(
            @"CPU(%) = @@91.2@@ perc", "@@");
        Assert.NotNull(rule);
        Assert.Equal("55.0",
            UiTreeMonitor.Core.Filters.ValueExtractor.ExtractOne(@"CPU(%) = 55.0 perc", rule!));
    }

    [Fact]
    public void CompileMarkedSample_DigitSuffixFuzzy_ExtractsIntegerPart()
    {
        // 需求2：示例 Value = "@@603@@.561"（只圈整数部分）——后缀 .561 的数字是占位，
        // 实际值小数变化（605.123）时仍匹配 → 提取 605（数字可被图表识别）
        var rule = UiTreeMonitor.Core.Filters.ValueExtractor.CompileFromMarkedSample(
            "Value = \"@@603@@.561\"", "@@");
        Assert.NotNull(rule);
        Assert.Equal("605",
            UiTreeMonitor.Core.Filters.ValueExtractor.ExtractOne("Value = \"605.123\"", rule!));
    }

    [Fact]
    public void CompileMarkedSample_DigitLeadingSuffix_CapturesAtLeastOneChar()
    {
        // 标记夹在任意位置都要能提取：Value = "@@6@@01.582"——后缀锚点以数字开头
        // （模糊化后 \d+\.\d+ 起头），捕获组必须至少占 1 字符，否则非贪婪退化匹配空串。
        // 对实际值 601.582 → 提取 6（数值）
        var rule = UiTreeMonitor.Core.Filters.ValueExtractor.CompileFromMarkedSample(
            "Value = \"@@6@@01.582\"", "@@");
        Assert.NotNull(rule);
        Assert.Equal("6",
            UiTreeMonitor.Core.Filters.ValueExtractor.ExtractOne("Value = \"601.582\"", rule!));
    }

    [Fact]
    public void CompileMarkedSample_DigitDigitBoundary_TakesMarkedLength()
    {
        // 数字对数字交界按示例圈住的位数取：Value = "@@66@@3.631"——捕获 66（2位）以数字结尾、
        // 后缀 3.631 以数字开头（两侧都模糊成 \d+，边界不确定）→ 以圈住字符数为下限取最短
        // → 对实际值 663.631 提取 66（不是 6 也不是 60）
        var rule = UiTreeMonitor.Core.Filters.ValueExtractor.CompileFromMarkedSample(
            "Value = \"@@66@@3.631\"", "@@");
        Assert.NotNull(rule);
        Assert.Equal("66",
            UiTreeMonitor.Core.Filters.ValueExtractor.ExtractOne("Value = \"663.631\"", rule!));
    }

    [Fact]
    public void CompileMarkedSample_TextCapture_StillCollected()
    {
        // 需求3：圈住文本（Value ）也要能提取采集；后缀 ="603.561" 数字模糊后照常匹配实际值
        var rule = UiTreeMonitor.Core.Filters.ValueExtractor.CompileFromMarkedSample(
            "Value = \"@@Value @@=\"603.561\"", "@@");
        Assert.NotNull(rule);
        Assert.Equal("Value ",
            UiTreeMonitor.Core.Filters.ValueExtractor.ExtractOne("Value = \"Value =\"605.123\"", rule!));
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
        // 值1：锚点提取（编译自标记示例）；值2：正则。顺序=规则顺序
        var rule1 = UiTreeMonitor.Core.Filters.ValueExtractor.CompileFromMarkedSample("x @@42@@ y", "@@");
        var rules = new[] { rule1!, @"re:(\d+)" };
        var result = UiTreeMonitor.Core.Filters.ValueExtractor.ExtractAll("x 7 y", rules);
        Assert.Equal(2, result.Count);
        Assert.Equal("7", result[0]);    // 值1：前缀 "x " 与后缀 " y" 之间的内容
        Assert.Equal("7", result[1]);    // 值2：正则第一捕获组
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

public class ElementMatcherTests
{
    // 复现用户场景：Text 元素的 Name 就是被监控的变化值（实时数值文本）
    private static readonly ElementNode MenuNode = new() { Id = 1, Name = "系统", ControlType = "MenuItem" };
    private static readonly ElementNode ValueNode = new() { Id = 2, Name = "677.941", ControlType = "Text" };
    private static readonly ElementNode OtherTextNode = new() { Id = 3, Name = "标题", ControlType = "Text" };

    private static ElementNode[] Nodes => new[] { MenuNode, ValueNode, OtherTextNode };

    [Fact]
    public void ExactMatch_WhenNameStillCurrent_Level0()
    {
        var (node, level) = ElementMatcher.FindNode(Nodes, "677.941", null, "Text");
        Assert.NotNull(node);
        Assert.Equal(2, node!.Id);
        Assert.Equal(0, level);
    }

    [Fact]
    public void NameChanged_IgnoredNameFallback_Level1()
    {
        // 创建任务时 Name="0.185013"，运行时元素 Name 已变成 "677.941" → 精确匹配失败，
        // 降级忽略 Name 后仍按 ControlType=Text 定位到同一节点——值变化监控不中断
        var (node, level) = ElementMatcher.FindNode(Nodes, "0.185013", null, "Text");
        Assert.NotNull(node);
        Assert.Equal(2, node!.Id);
        Assert.Equal(1, level);
    }

    [Fact]
    public void AutomationId_PinsNode_EvenWhenNameChanged()
    {
        var pinned = new ElementNode { Id = 9, Name = "新的值", ControlType = "Text", AutomationId = "valueText" };
        var others = new[] { MenuNode, new() { Id = 4, Name = "x", ControlType = "Text" }, pinned };
        var (node, level) = ElementMatcher.FindNode(others, "旧值", "valueText", "Text");
        Assert.NotNull(node);
        Assert.Equal(9, node!.Id);
        Assert.Equal(1, level);
    }

    [Fact]
    public void TypeOnlyFallback_TakesFirstSameType_Level2()
    {
        // Name 与 AutomationId 都不可靠时，仅按 ControlType 取第一个同类型节点
        var (node, level) = ElementMatcher.FindNode(Nodes, "不存在的名字", "不存在的AutoId", "Text");
        Assert.NotNull(node);
        Assert.Equal(2, node!.Id); // 第一个 Text 节点
        Assert.Equal(2, level);
    }

    [Fact]
    public void NoMatchAtAll_ReturnsNull()
    {
        var (node, level) = ElementMatcher.FindNode(Nodes, "名字", null, "Button");
        Assert.Null(node);
        Assert.Equal(-1, level);
    }
}
