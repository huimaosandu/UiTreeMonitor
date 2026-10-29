// 值提取器：把采样原始值按规则截取为多个子值（如 @@614.466@@ → 614.466）。
// 规则语法：
//   "@@"            标记对：截取 起止标记（同串）之间的内容，内部转成正则 Escape(标记)+"(.*?)"+Escape(标记) 非贪婪匹配
//   "re:(\d+\.?)"   正则模式：取第一个捕获组（无捕获组则取整体匹配）
//   其他任意串      按标记对处理（如 "##" → ##(.*?)##），允许自定义标记
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace UiTreeMonitor.Core.Filters;

/// <summary>值提取器：规则 → 值序列。</summary>
public static class ValueExtractor
{
    /// <summary>正则模式前缀。</summary>
    private const string RegexPrefix = "re:";

    /// <summary>对原始值应用规则列表，返回 值1..值N（规则顺序 = 值顺序）。规则空时返回原始值。</summary>
    public static List<string> ExtractAll(string? rawValue, IReadOnlyList<string> rules)
    {
        if (rules is null || rules.Count == 0)
            return new List<string> { rawValue ?? "" };

        var result = new List<string>(rules.Count);
        foreach (var rule in rules)
            result.Add(ExtractOne(rawValue, rule));
        return result;
    }

    /// <summary>应用单条规则。无匹配返回空串（保持列对齐：值N 永远存在）。</summary>
    public static string ExtractOne(string? rawValue, string rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
            return rawValue ?? "";
        rawValue ??= "";

        try
        {
            if (rule.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // 正则模式：第一捕获组优先，无捕获组取整体匹配
                var pattern = rule[RegexPrefix.Length..];
                var m = Regex.Match(rawValue, pattern, RegexOptions.CultureInvariant);
                if (!m.Success) return "";
                return m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
            }

            // 标记对模式：标记(.*?)标记，非贪婪，取第一个匹配
            var esc = Regex.Escape(rule.Trim());
            var mm = Regex.Match(rawValue, $"{esc}(.*?){esc}", RegexOptions.CultureInvariant);
            return mm.Success ? mm.Groups[1].Value : "";
        }
        catch (ArgumentException)
        {
            return ""; // 用户正则非法时该列置空，不影响其他列
        }
    }
}
