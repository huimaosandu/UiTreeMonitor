// 正则过滤：包含性（缩小树范围）+ 提取性（命名组提取关注值）
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UiTreeMonitor.Core.Models;

namespace UiTreeMonitor.Core.Filters;

/// <summary>单条过滤规则：作用于某个属性，可只保留匹配节点或提取命名组值。</summary>
public sealed class FilterRule
{
    public string Property { get; set; } = UiProperties.Name;
    public string Pattern { get; set; } = "";
    public bool IgnoreCase { get; set; }
    public bool IsExtract { get; set; }        // true=提取命名组作为"关注值"；false=仅包含性过滤
    public string ExtractGroup { get; set; } = "value"; // 提取的命名组名

    private Regex? _compiled;
    public Regex ToRegex()
    {
        _compiled ??= new Regex(Pattern,
            RegexOptions.Compiled | (IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
            TimeSpan.FromSeconds(1)); // 超时保护，防灾难性回溯
        return _compiled;
    }

    public bool IsMatch(string? value) => value is not null && ToRegex().IsMatch(value);

    /// <summary>提取命名组值；未匹配或组不存在返回 null。</summary>
    public string? Extract(string? value)
    {
        if (value is null) return null;
        var m = ToRegex().Match(value);
        if (!m.Success) return null;
        var g = m.Groups[ExtractGroup];
        return g.Success ? g.Value : null;
    }
}

/// <summary>一组过滤规则的组合（AND / OR）。</summary>
public sealed class FilterSet
{
    public IReadOnlyList<FilterRule> Rules { get; set; } = Array.Empty<FilterRule>();
    public bool IsAnd { get; set; } = true;

    public bool IsEmpty => Rules.Count == 0;

    /// <summary>节点是否通过包含性过滤（IsExtract 规则不参与包含判断，只做提取）。</summary>
    public bool Passes(ElementNode node)
    {
        if (IsEmpty) return true;
        bool hasFilterRule = false;
        foreach (var r in Rules) if (!r.IsExtract) { hasFilterRule = true; break; }
        if (!hasFilterRule) return true; // 纯提取场景：不限制范围

        bool MatchAny() { foreach (var r in Rules) { if (r.IsExtract) continue; if (r.IsMatch(node.GetProperty(r.Property))) return true; } return false; }
        bool MatchAll() { foreach (var r in Rules) { if (r.IsExtract) continue; if (!r.IsMatch(node.GetProperty(r.Property))) return false; } return true; }
        return IsAnd ? MatchAll() : MatchAny();
    }

    /// <summary>对该节点的每个属性做提取，返回 {属性: 提取值}（仅 IsExtract 规则）。</summary>
    public IReadOnlyDictionary<string, string> ExtractValues(ElementNode node)
    {
        var result = new Dictionary<string, string>();
        foreach (var r in Rules)
        {
            if (!r.IsExtract) continue;
            var v = r.Extract(node.GetProperty(r.Property));
            if (v is not null) result[$"{r.Property}@{r.Pattern}"] = v;
        }
        return result;
    }

    /// <summary>过滤一棵树：返回通过规则的节点 id 集合（含所有祖先，保证树结构完整）。</summary>
    public IReadOnlySet<int> FilterTree(TreeSnapshot tree)
    {
        if (IsEmpty)
            return new HashSet<int>(tree.Nodes.Keys);

        var keep = new HashSet<int>();
        // 先标记命中的节点
        foreach (var (id, node) in tree.Nodes)
            if (Passes(node)) keep.Add(id);

        // 回溯补全祖先
        var all = tree.Nodes;
        foreach (var id in keep.ToList())
        {
            int cur = id;
            while (cur != -1 && all.TryGetValue(cur, out var n) && n.ParentId != -1)
            {
                if (!keep.Contains(n.ParentId)) keep.Add(n.ParentId);
                cur = n.ParentId;
            }
        }
        return keep;
    }
}
