using System;
using System.Collections.Generic;
using System.IO;
using UiTreeMonitor.Core;
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

    private static TreeSnapshot TreeOf(params ElementNode[] nodes)
    {
        var dict = new Dictionary<int, ElementNode>();
        var order = new List<int>();
        foreach (var n in nodes) { dict[n.Id] = n; order.Add(n.Id); }
        return new TreeSnapshot { RootId = nodes.Length > 0 ? nodes[0].Id : -1, Nodes = dict, Order = order };
    }

    private static TreeSnapshot Nodes => TreeOf(MenuNode, ValueNode, OtherTextNode);

    [Fact]
    public void ExactMatch_WhenNameStillCurrent_Level1()
    {
        // 无结构路径：Name 快照仍有效时精确属性命中（级别1）
        var (node, level) = ElementMatcher.FindNode(Nodes, null, "677.941", null, "Text");
        Assert.NotNull(node);
        Assert.Equal(2, node!.Id);
        Assert.Equal(1, level);
    }

    [Fact]
    public void NameChanged_NoPathNoFallback_ReturnsNull()
    {
        // 创建任务时 Name="0.185013"，运行时元素 Name 已变成 "677.941"：
        // 无结构路径时精确匹配失败 → 不放宽兜底（取错节点比漏采更糟）→ 返回 null 跳过本轮
        // Name 变化场景由结构路径覆盖（见 ElementPathTests）
        var (node, level) = ElementMatcher.FindNode(Nodes, null, "0.185013", null, "Text");
        Assert.Null(node);
        Assert.Equal(-1, level);
    }

    [Fact]
    public void TypeOnlyMatch_NoFallback_ReturnsNull()
    {
        // Type 对但 Name/AutoId 不匹配：不做"仅 ControlType"兜底（曾导致定位到非目标 Text）
        var (node, _) = ElementMatcher.FindNode(Nodes, null, "不存在的名字", "不存在的AutoId", "Text");
        Assert.Null(node);
    }

    [Fact]
    public void NoMatchAtAll_ReturnsNull()
    {
        var (node, level) = ElementMatcher.FindNode(Nodes, null, "名字", null, "Button");
        Assert.Null(node);
        Assert.Equal(-1, level);
    }
}

/// <summary>结构索引路径定位：唯一锁定（值变化 / AutomationId 缺失或重复 / 同级同类型多元素场景）。</summary>
public class ElementPathTests
{
    // 复现用户不稳定场景：同窗口多个 Text、无 AutomationId——属性降级无法区分，结构路径唯一锁定
    // 树形：Window(1) ├─ Text(2) "状态文本"
    //                  └─ Text(3) "0.185013" └─ Text(4) "677.941"（被监控的实时数值）
    private static TreeSnapshot BuildSampleTree() => new()
    {
        RootId = 1,
        Nodes = new Dictionary<int, ElementNode>
        {
            [1] = new() { Id = 1, ParentId = -1, ControlType = "Window", Children = new[] { 2, 3 } },
            [2] = new() { Id = 2, ParentId = 1, ControlType = "Text", Name = "状态文本" },
            [3] = new() { Id = 3, ParentId = 1, ControlType = "Text", Name = "0.185013", Children = new[] { 4 } },
            [4] = new() { Id = 4, ParentId = 3, ControlType = "Text", Name = "677.941" },
        },
        Order = new[] { 1, 2, 3, 4 },
    };

    [Fact]
    public void BuildPath_ComputesChildIndexes()
    {
        var tree = BuildSampleTree();
        Assert.Equal("", ElementMatcher.BuildPath(tree, 1));    // 根 → 空路径
        Assert.Equal("0", ElementMatcher.BuildPath(tree, 2));   // 根的第 0 个子
        Assert.Equal("1/0", ElementMatcher.BuildPath(tree, 4)); // 根第 1 子的第 0 子
        Assert.Null(ElementMatcher.BuildPath(tree, 99));        // 不在树内
    }

    [Fact]
    public void FindByPath_ResolvesSameNode()
    {
        var node = ElementMatcher.FindByPath(BuildSampleTree(), "1/0");
        Assert.NotNull(node);
        Assert.Equal(4, node!.Id);
    }

    [Fact]
    public void FindByPath_OutOfRangeOrInvalid_ReturnsNull()
    {
        var tree = BuildSampleTree();
        Assert.Null(ElementMatcher.FindByPath(tree, "9"));   // 子索引越界
        Assert.Null(ElementMatcher.FindByPath(tree, "1/9")); // 深层越界
        Assert.Null(ElementMatcher.FindByPath(tree, "x/y")); // 非数字段
        Assert.Null(ElementMatcher.FindByPath(tree, null));  // 未配置
    }

    [Fact]
    public void FindNode_Tree_PathTakesPriorityOverAttr_Level0()
    {
        // 多个 Text 且无 AutomationId：属性匹配只能取第一个；路径直接锁定 4 号节点
        var (node, level) = ElementMatcher.FindNode(BuildSampleTree(), "1/0", "0.185013", null, "Text");
        Assert.NotNull(node);
        Assert.Equal(4, node!.Id);
        Assert.Equal(0, level);
    }

    [Fact]
    public void FindNode_Tree_ValueChanged_PathStillLocksSameNode()
    {
        // 被监控的 Name 持续变化（677.941 → 888.888 → …）：路径定位不受任何影响，无需属性降级
        var tree = BuildSampleTree();
        var nodes = new Dictionary<int, ElementNode>(tree.Nodes) { [4] = tree.Nodes[4] with { Name = "888.888" } };
        var changed = new TreeSnapshot { RootId = 1, Nodes = nodes, Order = tree.Order };
        var (node, level) = ElementMatcher.FindNode(changed, "1/0", "677.941", null, "Text");
        Assert.NotNull(node);
        Assert.Equal(4, node!.Id); // 仍是创建任务时锁定的同一节点
        Assert.Equal(0, level);
    }

    [Fact]
    public void FindNode_Tree_PathInvalid_FallsBackToAttr()
    {
        // 结构变化（子索引越界）→ 降级为精确属性匹配（Name+AutoId+Type 全条件命中 → 级别1）
        var (node, level) = ElementMatcher.FindNode(BuildSampleTree(), "5", "状态文本", null, "Text");
        Assert.NotNull(node);
        Assert.Equal(2, node!.Id);
        Assert.Equal(1, level);
    }

    [Fact]
    public void FindByRect_MatchesNodeWithSameRectAndType()
    {
        // 悬停/烧杯捕获场景：屏幕矩形 "L,T,R,B"（UIA 同源物理像素）+ ControlType 相等 → 唯一命中
        // Name 是实时变化值（树快照时 Name 可能已不同）不影响矩形定位
        var tree = BuildSampleTree();
        var changed = new Dictionary<int, ElementNode>(tree.Nodes)
        {
            [4] = tree.Nodes[4] with { Name = "888.888", BoundingRectangle = "10,20,110,60" },
        };
        var t2 = new TreeSnapshot { RootId = 1, Nodes = changed, Order = tree.Order };

        var node = ElementMatcher.FindByRect(t2, "10,20,110,60", "Text");
        Assert.NotNull(node);
        Assert.Equal(4, node!.Id);

        // 矩形对但 Type 不对 → 不命中（防巧合）；矩形为空 → 不命中
        Assert.Null(ElementMatcher.FindByRect(t2, "10,20,110,60", "Button"));
        Assert.Null(ElementMatcher.FindByRect(t2, null, "Text"));
        Assert.Null(ElementMatcher.FindByRect(t2, "99,99,199,99", "Text"));
    }
}

/// <summary>同步采集组：MonitorTask 外部驱动模式（严格同刻时间戳、结构路径定位、状态流转、停止条件）。</summary>
public class SyncGroupExternalModeTests
{
    private static MonitorTaskConfig MakeConfig(string name, string elemName = "677.941",
        IStopConditionSpec? stop = null, string? elemPath = null) => new()
    {
        Name = name,
        Target = new MonitorTarget
        {
            Kind = TargetKind.WindowHandle, Value = "0x1",
            ElementName = elemName, ElementControlType = "Text",
            ElementPath = elemPath,
        },
        Properties = new[] { "Name" },
        Filter = new FilterSet(),
        Timer = new TimerSpec { IntervalMs = 500 },
        StopCondition = stop ?? new NeverStopSpec(),
    };

    // SelectMonitoredValues 按 tree.Order 前序遍历，快照必须带 Order；节点 2 的结构路径 = "0"
    private static TreeSnapshot TreeWith(params ElementNode[] nodes)
    {
        var dict = new Dictionary<int, ElementNode>();
        var order = new List<int>();
        foreach (var n in nodes) { dict[n.Id] = n; order.Add(n.Id); }
        return new TreeSnapshot { RootId = nodes.Length > 0 ? nodes[0].Id : -1, Nodes = dict, Order = order };
    }

    private static TreeSnapshot ValueTree(string name) => TreeWith(
        new ElementNode { Id = 1, ParentId = -1, Name = "根", ControlType = "Window", Children = new[] { 2 } },
        new ElementNode { Id = 2, ParentId = 1, Name = name, ControlType = "Text" });

    [Fact]
    public void RecordExternalSample_TwoTasks_SameTick_ExactSameTimestamp()
    {
        // 核心承诺：同一轮 tick 推给多个任务，各任务记录的 Timestamp 严格等于 tickAt。
        // 任务A 靠精确属性命中；任务B 的 Name 快照（888.888）已过期——靠结构路径 "0" 锁定同一节点
        var t1 = new MonitorTask(MakeConfig("任务A"));
        var t2 = new MonitorTask(MakeConfig("任务B", elemName: "888.888", elemPath: "0"));
        t1.ExternallyDriven = true; t1.StartExternal();
        t2.ExternallyDriven = true; t2.StartExternal();
        var tree = ValueTree("677.941");

        var tickAt = new DateTime(2026, 10, 30, 15, 0, 0, 123);
        t1.RecordExternalSample(tickAt, tree);
        t2.RecordExternalSample(tickAt, tree);

        Assert.NotEmpty(t1.Records);
        Assert.NotEmpty(t2.Records);
        Assert.All(t1.Records, r => Assert.Equal(tickAt, r.Timestamp));
        Assert.All(t2.Records, r => Assert.Equal(tickAt, r.Timestamp));
        // 任务B 靠路径锁定的节点实际值是 677.941（Name 快照过期不影响）
        Assert.Equal("677.941", t2.Records[0].Value);
    }

    [Fact]
    public void RecordExternalSample_NameChanged_WithElemPath_StillRecords()
    {
        // 创建任务时 Name=0.185013（已过期）+ 结构路径 "0"：路径不看 Name → 锁定节点持续记录实际值
        var task = new MonitorTask(MakeConfig("任务A", elemName: "0.185013", elemPath: "0"));
        task.ExternallyDriven = true;
        task.StartExternal();
        task.RecordExternalSample(DateTime.Now, ValueTree("677.941"));
        Assert.NotEmpty(task.Records);
        Assert.Equal("677.941", task.Records[0].Value);
    }

    [Fact]
    public void RecordExternalSample_NameChanged_NoPath_SkipsRecord()
    {
        // 无结构路径 + Name 已变化 → 精确匹配失败，不做放宽兜底（取错节点比漏采更糟）：跳过本轮不记空点/错点
        var task = new MonitorTask(MakeConfig("任务A", elemName: "0.185013"));
        task.ExternallyDriven = true;
        task.StartExternal();
        task.RecordExternalSample(DateTime.Now, ValueTree("677.941"));
        Assert.Empty(task.Records);
    }

    [Fact]
    public void StartExternal_StateTransitions_IdleRunningStopped()
    {
        var task = new MonitorTask(MakeConfig("任务A"));
        Assert.Equal(TaskRunState.Idle, task.State);

        task.ExternallyDriven = true;
        task.StartExternal();
        Assert.Equal(TaskRunState.Running, task.State);

        task.RequestStop();
        Assert.True(task.IsStopRequested);
        task.MarkStopped();
        Assert.Equal(TaskRunState.Stopped, task.State);

        // Stopped 可重新同步启动
        task.StartExternal();
        Assert.Equal(TaskRunState.Running, task.State);
    }

    [Fact]
    public void RecordExternalSample_SampleCountStop_CompletesAfterCount()
    {
        var task = new MonitorTask(MakeConfig("任务A", stop: new SampleCountStopSpec { Count = 2 }));
        task.ExternallyDriven = true;
        task.StartExternal();
        var tree = ValueTree("677.941");

        task.RecordExternalSample(DateTime.Now, tree);
        Assert.Equal(TaskRunState.Running, task.State);
        task.RecordExternalSample(DateTime.Now.AddMilliseconds(500), tree);
        Assert.Equal(TaskRunState.Completed, task.State); // 达到次数 → 自动完成
    }

    [Fact]
    public void MarkCompleted_FromRunning_SetsCompleted()
    {
        // 协调器在组监控时长到期时统一调用 MarkCompleted：全组同时完成
        var task = new MonitorTask(MakeConfig("任务A"));
        task.ExternallyDriven = true;
        task.StartExternal();
        Assert.Equal(TaskRunState.Running, task.State);

        task.MarkCompleted();
        Assert.Equal(TaskRunState.Completed, task.State);

        // 已完成状态再调停止/完成均为 no-op（协调器兜底段不覆盖终态）
        task.MarkStopped();
        task.MarkCompleted();
        Assert.Equal(TaskRunState.Completed, task.State);
    }
}

/// <summary>AppLog 行为：每次启动新建一份日志（启动时刻命名）；文件被删/被占用时继续保存不丢日志。</summary>
public class AppLogTests : IDisposable
{
    // AppLog 目录/文件在进程内只解析一次（Lazy）——本类所有测试共用同一份日志文件，Dispose 统一清理
    public void Dispose()
    {
        try
        {
            File.Delete(AppLog.LogFilePath);
            var baseName = Path.GetFileNameWithoutExtension(AppLog.LogFilePath);
            for (int i = 1; i <= 9; i++)
                File.Delete(Path.Combine(AppLog.LogDir, $"{baseName}_{i}.log"));
        }
        catch { }
    }

    [Fact]
    public void LogFilePath_NamedByStartupTimestamp()
    {
        // 每次打开软件都新建一份日志：文件名 = 启动时刻 yyyyMMdd_HHmmss
        Assert.Matches(new System.Text.RegularExpressions.Regex(@"^\d{8}_\d{6}\.log$"),
            Path.GetFileName(AppLog.LogFilePath));
    }

    [Fact]
    public void Write_DeletedLogFile_RecreatesAndKeepsSaving()
    {
        // 测试过程中日志被删除 → 后续写入自动重建文件，日志继续保存
        AppLog.Write("TEST", "before delete");
        Assert.True(File.Exists(AppLog.LogFilePath));

        File.Delete(AppLog.LogFilePath);
        Assert.False(File.Exists(AppLog.LogFilePath));

        AppLog.Write("TEST", "after delete");
        Assert.True(File.Exists(AppLog.LogFilePath));
        Assert.Contains("after delete", File.ReadAllText(AppLog.LogFilePath));
    }

    [Fact]
    public void Write_FileLockedByOthers_FallsBackToAlternateFile()
    {
        // 日志被外部进程独占（或删除后句柄未释放 delete-pending）：降级备用文件继续保存
        AppLog.Write("TEST", "warmup");
        using (new FileStream(AppLog.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AppLog.Write("TEST", "while locked");
        }

        var baseName = Path.GetFileNameWithoutExtension(AppLog.LogFilePath);
        var alt = Path.Combine(AppLog.LogDir, $"{baseName}_1.log");
        Assert.True(File.Exists(alt));
        Assert.Contains("while locked", File.ReadAllText(alt));
    }
}

/// <summary>录制任务模型：树形步骤 JSON 持久化 round-trip 与列表展示文本。</summary>
public class RecordingTaskTests
{
    [Fact]
    public void ToFromJson_NestedTree_PreservesAllFields()
    {
        // 嵌套结构：loop 内含 delay+trigger，trigger 又带子步骤（多层嵌套的最小全类型覆盖）
        var task = new RecordingTask
        {
            Name = "冒烟任务",
            Mode = "uia",
            Steps =
            {
                new StepNode
                {
                    Kind = StepKinds.Action,
                    Action = new ActionRecord
                    {
                        Timestamp = new DateTime(2026, 11, 4, 12, 0, 0),
                        ElementPath = "spec:v1|test",
                        ElementName = "开始按钮",
                        PatternName = "InvokePattern",
                        MethodName = "Invoke",
                        Parameters = new object?[] { "参数1" },
                        Success = true,
                        Result = "OK",
                    },
                },
                new StepNode { Kind = StepKinds.Delay, DelayMs = 2500, IsEnabled = false },
                new StepNode { Kind = StepKinds.Timer, TriggerAt = new DateTime(2026, 12, 25, 8, 30, 0) },
                // 截图步骤：spec 定位 + 显示名 + 保存目录（运行时 img-时间戳.png 自动存入该目录）
                new StepNode
                {
                    Kind = StepKinds.Screenshot,
                    ScreenshotSpec = "spec:v1|autoId=btnSave",
                    ScreenshotName = "保存按钮",
                    ScreenshotDir = @"D:\uitreemonitor\shots",
                },
                new StepNode
                {
                    Kind = StepKinds.Loop,
                    LoopCount = 5,
                    LoopIntervalMs = 200,
                    Children =
                    {
                        new StepNode { Kind = StepKinds.Delay, DelayMs = 300 },
                        new StepNode
                        {
                            Kind = StepKinds.Trigger,
                            Trigger = new ElementTriggerSpec
                            {
                                ElementSpec = "coord:v1|100,200",
                                ElementName = "温度显示",
                                Property = "Value",
                                Condition = "Greater",
                                ExpectedValue = "100",
                                TimeoutSec = 30,
                                PollIntervalMs = 500,
                            },
                            Children = { new StepNode { Kind = StepKinds.Delay, DelayMs = 50 } },
                        },
                    },
                },
            },
        };

        var back = RecordingTask.FromJson(task.ToJson());

        Assert.NotNull(back);
        Assert.Equal(task.Id, back!.Id);                       // Guid 往返保持
        Assert.Equal("冒烟任务", back.Name);
        Assert.Equal("uia", back.Mode);
        Assert.Equal(5, back.Steps.Count);

        // action：全部字段（Parameters 往返后元素为 JsonElement，仅验证结构）
        var action = back.Steps[0];
        Assert.True(action.IsEnabled);
        Assert.NotNull(action.Action);
        Assert.Equal(new DateTime(2026, 11, 4, 12, 0, 0), action.Action!.Timestamp);
        Assert.Equal("spec:v1|test", action.Action.ElementPath);
        Assert.Equal("开始按钮", action.Action.ElementName);
        Assert.Equal("InvokePattern", action.Action.PatternName);
        Assert.Equal("Invoke", action.Action.MethodName);
        Assert.True(action.Action.Success);
        Assert.Equal("OK", action.Action.Result);
        Assert.Single(action.Action.Parameters!);

        // delay：字段 + 禁用标记
        var delay = back.Steps[1];
        Assert.Equal(StepKinds.Delay, delay.Kind);
        Assert.False(delay.IsEnabled);
        Assert.Equal(2500, delay.DelayMs);

        // timer：触发时刻
        Assert.Equal(new DateTime(2026, 12, 25, 8, 30, 0), back.Steps[2].TriggerAt);

        // screenshot：spec/显示名/保存目录三字段完整往返
        var shot = back.Steps[3];
        Assert.Equal(StepKinds.Screenshot, shot.Kind);
        Assert.Equal("spec:v1|autoId=btnSave", shot.ScreenshotSpec);
        Assert.Equal("保存按钮", shot.ScreenshotName);
        Assert.Equal(@"D:\uitreemonitor\shots", shot.ScreenshotDir);

        // loop：参数与两层嵌套
        var loop = back.Steps[4];
        Assert.Equal(StepKinds.Loop, loop.Kind);
        Assert.Equal(5, loop.LoopCount);
        Assert.Equal(200, loop.LoopIntervalMs);
        Assert.Equal(LoopEndModes.Count, loop.LoopEndMode);   // 旧格式无该字段 → 默认 count（向后兼容）
        Assert.Equal(2, loop.Children.Count);
        Assert.Equal(300, loop.Children[0].DelayMs);

        // trigger：完整触发器配置 + 子步骤
        var trig = loop.Children[1];
        Assert.Equal(StepKinds.Trigger, trig.Kind);
        Assert.NotNull(trig.Trigger);
        Assert.Equal("coord:v1|100,200", trig.Trigger!.ElementSpec);
        Assert.Equal("温度显示", trig.Trigger.ElementName);
        Assert.Equal("Value", trig.Trigger.Property);
        Assert.Equal("Greater", trig.Trigger.Condition);
        Assert.Equal("100", trig.Trigger.ExpectedValue);
        Assert.Equal(30, trig.Trigger.TimeoutSec);
        Assert.Equal(500, trig.Trigger.PollIntervalMs);
        Assert.Single(trig.Children);
        Assert.Equal(50, trig.Children[0].DelayMs);
    }

    [Fact]
    public void FromJson_Invalid_ReturnsNull()
    {
        Assert.Null(RecordingTask.FromJson("{ 不是合法 JSON"));
    }

    [Fact]
    public void DisplayText_AllKinds_Formatted()
    {
        // 循环：无间隔不显示间隔段，有间隔显示毫秒数
        Assert.Equal("🔁 循环 ×3", new StepNode { Kind = StepKinds.Loop, LoopCount = 3, LoopIntervalMs = 0 }.DisplayText);
        Assert.Contains("每轮间隔 500ms", new StepNode { Kind = StepKinds.Loop }.DisplayText);
        Assert.Equal("⏱ 延时 1200ms", new StepNode { Kind = StepKinds.Delay, DelayMs = 1200 }.DisplayText);
        Assert.Equal("⏰ 定时 2026-11-04 09:05:00",
            new StepNode { Kind = StepKinds.Timer, TriggerAt = new DateTime(2026, 11, 4, 9, 5, 0) }.DisplayText);

        // 触发器：元素名 + 属性 + 条件 + 期望值；无子步骤 Detail 标注纯等待点
        var trig = new StepNode
        {
            Kind = StepKinds.Trigger,
            Trigger = new ElementTriggerSpec { ElementName = "温度", Property = "Value", Condition = "Greater", ExpectedValue = "80" },
        };
        Assert.Equal("⚗ 触发器：温度 的 Value > 80", trig.DisplayText);
        Assert.Contains("纯等待点", trig.DetailText);

        // 动作：Pattern.Method 元素名
        Assert.Equal("InvokePattern.Invoke 按钮",
            new StepNode { Kind = StepKinds.Action, Action = new ActionRecord { PatternName = "InvokePattern", MethodName = "Invoke", ElementName = "按钮" } }
            .DisplayText);

        // 截图：元素名 + 保存目录；未配置 spec 时标注未配置
        var shot = new StepNode
        {
            Kind = StepKinds.Screenshot,
            ScreenshotSpec = "spec:v1|autoId=btnSave",
            ScreenshotName = "保存按钮",
            ScreenshotDir = @"D:\uitreemonitor\shots",
        };
        Assert.Equal("📷 截图 保存按钮", shot.DisplayText);
        Assert.Equal(@"保存于 D:\uitreemonitor\shots", shot.DetailText);
        Assert.Equal("📷 截图（未配置）", new StepNode { Kind = StepKinds.Screenshot }.DisplayText);
    }

    [Fact]
    public void LoopEndModes_RoundTrip_And_Predicates()
    {
        // round-trip：duration/deadline 两新模式字段完整往返（camelCase JSON）
        var task = new RecordingTask
        {
            Steps =
            {
                new StepNode { Kind = StepKinds.Loop, LoopEndMode = LoopEndModes.Duration, LoopDurationMs = 90_000, LoopIntervalMs = 0,
                    Children = { new StepNode { Kind = StepKinds.Delay, DelayMs = 100 } } },
                new StepNode { Kind = StepKinds.Loop, LoopEndMode = LoopEndModes.Deadline,
                    LoopDeadline = new DateTime(2026, 11, 5, 18, 0, 0), LoopIntervalMs = 0 },
            },
        };
        var back = RecordingTask.FromJson(task.ToJson());
        Assert.NotNull(back);
        Assert.Equal(LoopEndModes.Duration, back!.Steps[0].LoopEndMode);
        Assert.Equal(90_000, back.Steps[0].LoopDurationMs);
        Assert.Equal(LoopEndModes.Deadline, back.Steps[1].LoopEndMode);
        Assert.Equal(new DateTime(2026, 11, 5, 18, 0, 0), back.Steps[1].LoopDeadline);

        // 每轮判定：count=轮数未达；duration=时长未满；deadline=时刻未到/已过
        var count = new StepNode { Kind = StepKinds.Loop, LoopCount = 3, LoopIntervalMs = 0 };
        Assert.True(count.LoopHasMoreRounds(2, DateTime.Now));
        Assert.False(count.LoopHasMoreRounds(3, DateTime.Now));

        var dur = new StepNode { Kind = StepKinds.Loop, LoopEndMode = LoopEndModes.Duration, LoopDurationMs = 60_000 };
        var started = DateTime.Now;
        Assert.True(dur.LoopHasMoreRounds(0, started));                                  // 未满 60s
        Assert.False(dur.LoopHasMoreRounds(0, started.AddSeconds(-61)));                  // 启动至今已 61s

        var dl = new StepNode { Kind = StepKinds.Loop, LoopEndMode = LoopEndModes.Deadline,
            LoopDeadline = DateTime.Now.AddMinutes(5) };
        Assert.True(dl.LoopHasMoreRounds(0, DateTime.Now));
        dl.LoopDeadline = DateTime.Now.AddSeconds(-1);
        Assert.False(dl.LoopHasMoreRounds(0, DateTime.Now));                              // 已过=跳过循环体

        // 显示文本：三模式
        Assert.Equal("🔁 循环 ×3", count.DisplayText);
        Assert.Equal("🔁 循环 ≤2min", new StepNode
        { Kind = StepKinds.Loop, LoopEndMode = LoopEndModes.Duration, LoopDurationMs = 120_000, LoopIntervalMs = 0 }.DisplayText);
        Assert.Equal("🔁 循环 至 18:00:00", new StepNode
        { Kind = StepKinds.Loop, LoopEndMode = LoopEndModes.Deadline,
          LoopDeadline = new DateTime(2026, 11, 5, 18, 0, 0), LoopIntervalMs = 0 }.DisplayText);

        // 时长格式化：整除取大单位（90s 非整分钟 → 秒）
        Assert.Equal("2h", StepNode.FormatDuration(7_200_000));
        Assert.Equal("5min", StepNode.FormatDuration(300_000));
        Assert.Equal("90s", StepNode.FormatDuration(90_000));
        Assert.Equal("30s", StepNode.FormatDuration(30_000));
        Assert.Equal("1500ms", StepNode.FormatDuration(1_500));
    }
}
