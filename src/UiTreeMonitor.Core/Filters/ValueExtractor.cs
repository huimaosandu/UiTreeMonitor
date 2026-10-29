// 值提取器：把采样原始值按规则截取为多个子值。
// 规则语法（存储/传入的均为 .NET 正则 pattern）：
//   锚点正则     由标记示例编译而来（CompileFromMarkedSample）：如示例 Value = "@@614.466@@"
//                → Escape(前缀)+"(.*?)"+Escape(后缀)，运行时对实际值取第一捕获组 → 614.466
//   "re:(\d+\.?)" 兼容写法：剥离 re: 前缀后按正则处理（第一捕获组优先，无捕获组取整体匹配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UiTreeMonitor.Core.Filters;

/// <summary>值提取器：规则 → 值序列。</summary>
public static class ValueExtractor
{
    /// <summary>正则模式前缀（兼容手写规则的写法，剥离后按正则处理）。</summary>
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

    /// <summary>
    /// 把"用户在示例值中插入标记"的文本编译为锚点提取正则：
    /// 解析第一个标记与第二个标记的位置，标记前文本=前缀锚点、标记之间=捕获组、标记后文本=后缀锚点。
    /// 锚点中的连续数字段模糊为 \d+ 通配（示例里的数字是"占位"：如示例后缀 .561 对实际值 .123 也能匹配）。
    /// 示例：Value = "@@603.561@@"（标记 @@）→ \QValue = "\E(.*?)\Q"\E → 对实际值提取 603.561；
    ///       Value = "@@603@@.561" → 后缀 \.\d+ → 实际值 605.123 提取 605（整数部分）。
    /// 返回 null 表示文本中没有成对标记（无法编译）。
    /// </summary>
    public static string? CompileFromMarkedSample(string sample, string marker)
    {
        if (string.IsNullOrEmpty(marker) || string.IsNullOrEmpty(sample))
            return null;

        int i1 = sample.IndexOf(marker, StringComparison.Ordinal);
        if (i1 < 0) return null;
        int i2 = sample.IndexOf(marker, i1 + marker.Length, StringComparison.Ordinal);
        if (i2 < 0) return null;

        string prefix = sample[..i1];
        string suffix = sample[(i2 + marker.Length)..];

        var sb = new StringBuilder();
        if (prefix.Length > 0) sb.Append(AnchorPattern(prefix));
        // 有后缀锚点用非贪婪（取最短）；无后缀必须用贪婪（非贪婪会匹配空串）
        sb.Append(suffix.Length > 0 ? "(.*?)" : "(.*)");
        if (suffix.Length > 0) sb.Append(AnchorPattern(suffix));
        return sb.ToString();
    }

    /// <summary>锚点转正则：连续数字段 → \d+ 通配（示例数字视为占位符），其余字符按字面转义。</summary>
    private static string AnchorPattern(string text)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsDigit(text[i]))
            {
                while (i < text.Length && char.IsDigit(text[i])) i++;
                sb.Append(@"\d+");
            }
            else
            {
                sb.Append(Regex.Escape(text[i..(i + 1)]));
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 应用单条规则（正则 pattern，re: 前缀会被剥离）。
    /// 第一捕获组优先，无捕获组取整体匹配；无匹配/非法正则返回空串（保持列对齐：值N 永远存在）。
    /// </summary>
    public static string ExtractOne(string? rawValue, string rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
            return rawValue ?? "";
        rawValue ??= "";

        var pattern = rule.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase)
            ? rule[RegexPrefix.Length..]
            : rule;

        try
        {
            var m = Regex.Match(rawValue, pattern, RegexOptions.CultureInvariant);
            if (!m.Success) return "";
            return m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
        }
        catch (ArgumentException)
        {
            return ""; // 用户正则非法时该列置空，不影响其他列
        }
    }
}
