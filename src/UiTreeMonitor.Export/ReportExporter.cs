// 导出：CSV / JSON / HTML 报告
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UiTreeMonitor.Core.Tasks;

namespace UiTreeMonitor.Export;

/// <summary>导出器：把任务的采样历史导出为 CSV / JSON / HTML。</summary>
public sealed class ReportExporter
{
    private readonly string _outputRoot;

    public ReportExporter(string outputRoot)
    {
        _outputRoot = outputRoot;
        Directory.CreateDirectory(outputRoot);
    }

    /// <summary>导出全部格式，返回已生成的文件路径列表。输出到 {dataRoot}\{当日yyyyMMdd}\{任务名}_{时刻}\。</summary>
    public IReadOnlyList<string> ExportAll(
        string taskName, Guid taskId, MonitorTaskConfig config,
        IReadOnlyList<SampleRecord> records, DateTime startedAt, DateTime endedAt)
    {
        var dayDir = Path.Combine(_outputRoot, DateTime.Now.ToString("yyyyMMdd"));
        var dir = Path.Combine(dayDir, $"{Sanitize(taskName)}_{DateTime.Now:HHmmss}");
        Directory.CreateDirectory(dir);

        var files = new List<string>
        {
            ExportCsv(dir, records),
            ExportJson(dir, taskName, taskId, config, records, startedAt, endedAt),
            ExportHtml(dir, taskName, taskId, config, records, startedAt, endedAt),
        };
        return files;
    }

    private static string ExportCsv(string dir, IReadOnlyList<SampleRecord> records)
    {
        var path = Path.Combine(dir, "data.csv");
        var sb = new StringBuilder("时间戳,属性,值\n");
        foreach (var r in records)
            sb.AppendLine($"{r.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{Csv(r.Property)},{Csv(r.Value)}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    private static string ExportJson(string dir, string name, Guid id, MonitorTaskConfig config,
        IReadOnlyList<SampleRecord> records, DateTime startedAt, DateTime endedAt)
    {
        var path = Path.Combine(dir, "metadata.json");
        var payload = new
        {
            task = new { id, name, startedAt, endedAt, recordCount = records.Count },
            config = config,
            records = records.Select(r => new { r.Timestamp, r.NodeId, r.NodePath, r.Property, r.Value }),
        };
        File.WriteAllText(path,
            System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                }),
            new UTF8Encoding(true));
        return path;
    }

    private static string ExportHtml(string dir, string name, Guid id, MonitorTaskConfig config,
        IReadOnlyList<SampleRecord> records, DateTime startedAt, DateTime endedAt)
    {
        var path = Path.Combine(dir, "report.html");

        // 分组：属性 → 值随时间的迷你走势（文本表）
        var groups = records.GroupBy(r => r.Property);
        var rows = new StringBuilder();
        foreach (var g in groups)
        {
            rows.Append($"<tr><td rowspan=\"{Math.Min(g.Count(), 1)}\">{CsvHtml(g.Key)}</td>");
            rows.Append($"<td>{g.Count()} 条</td>");
            var first = g.First();
            var last = g.Last();
            rows.Append($"<td>{CsvHtml(first.Value)} → {CsvHtml(last.Value)}</td>");
            rows.Append($"<td>{first.Timestamp:HH:mm:ss} → {last.Timestamp:HH:mm:ss}</td></tr>");
        }

        var html = $@"<!DOCTYPE html>
<html lang=""zh""><head><meta charset=""utf-8""><title>{CsvHtml(name)} 监控报告</title>
<style>body{{font-family:Segoe UI,Microsoft YaHei,sans-serif;margin:24px;color:#222}}
h1{{font-size:20px}} h2{{font-size:16px;margin-top:24px}}
table{{border-collapse:collapse;width:100%;margin-top:8px}}
th,td{{border:1px solid #ccc;padding:6px 10px;text-align:left;font-size:13px}}
th{{background:#f0f0f0}} .meta{{color:#666;font-size:13px}}
.chart{{border:1px solid #e0e0e0;background:#fcfcfc;margin-top:8px}}</style></head>
<body>
<h1>监控报告：{CsvHtml(name)}</h1>
<p class=""meta"">任务ID: {id:N} | 开始: {startedAt:yyyy-MM-dd HH:mm:ss} | 结束: {endedAt:yyyy-MM-dd HH:mm:ss} | 记录数: {records.Count}</p>
<h2>配置摘要</h2>
<table><tr><th>目标</th><td>{CsvHtml(config.Target.ToString())}</td></tr>
<tr><th>属性</th><td>{string.Join(", ", config.Properties)}</td></tr>
<tr><th>过滤</th><td>{CsvHtml(config.Filter.Rules.Count > 0 ? string.Join("; ", config.Filter.Rules.Select(r => r.Property + " ~ " + r.Pattern)) : "无")}</td></tr>
<tr><th>触发器</th><td>{CsvHtml(config.Trigger.Kind)}</td></tr>
<tr><th>时间器</th><td>{CsvHtml(config.TimeBox.Boxes.Count > 0 ? string.Join("; ", config.TimeBox.Boxes.Select(b => b.Kind)) : "无")}</td></tr></table>
<h2>值变化走势</h2>
{BuildLineCharts(records)}
<h2>属性变化摘要</h2>
<table><tr><th>属性</th><th>条数</th><th>值变化</th><th>时间范围</th></tr>{rows}</table>
</body></html>";

        File.WriteAllText(path, html, new UTF8Encoding(true));
        return path;
    }

    /// <summary>
    /// 按属性分组生成 SVG 连线图（时间戳→X、数值→Y），数值不可解析的属性不出图。
    /// 纯内联 SVG，无外部依赖，离线可用。
    /// </summary>
    private static string BuildLineCharts(IReadOnlyList<SampleRecord> records)
    {
        var sb = new StringBuilder();
        foreach (var g in records.GroupBy(r => r.Property))
        {
            // 仅数值属性可画折线（InvariantCulture 解析）
            var pts = g
                .Select(r => (r.Timestamp, V: Parsed(r.Value)))
                .Where(p => p.V.HasValue)
                .Select(p => (p.Timestamp, V: p.V!.Value))
                .ToList();
            if (pts.Count < 2)
            {
                sb.Append($"<p class='meta'>属性 {CsvHtml(g.Key)}：数值样本不足（{pts.Count}），不生成走势图。</p>");
                continue;
            }

            sb.Append(SvgChart(g.Key, pts));
        }
        return sb.Length > 0 ? sb.ToString() : "<p class='meta'>无数值型采样数据，未生成走势图。</p>";
    }

    private static double? Parsed(string? s)
        => double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v
         : (double.TryParse(s, out var v2) ? v2 : null);

    /// <summary>单属性 SVG 折线图：边距 + 坐标轴刻度 + 数据折线。</summary>
    private static string SvgChart(string property, List<(DateTime T, double V)> pts)
    {
        const int W = 900, H = 260, ML = 64, MR = 16, MT = 14, MB = 30;
        int pw = W - ML - MR, ph = H - MT - MB;

        var t0 = pts[0].T.Ticks; var t1 = pts[^1].T.Ticks;
        var vMin = pts.Min(p => p.V); var vMax = pts.Max(p => p.V);
        if (Math.Abs(vMax - vMin) < 1e-12) { vMax += 1; vMin -= 1; } // 恒值时留出上下空间

        double X(DateTime t) => t1 == t0 ? ML + pw / 2.0 : ML + (double)(t.Ticks - t0) / (t1 - t0) * pw;
        double Y(double v) => MT + (vMax - v) / (vMax - vMin) * ph;

        var poly = string.Join(" ", pts.Select(p => $"{X(p.T):F1},{Y(p.V):F1}"));
        var area = $"{ML},{MT + ph} {poly} {ML + pw},{MT + ph}"; // 半透明填充

        // Y 轴 5 段刻度
        var yTicks = new StringBuilder();
        for (int i = 0; i <= 4; i++)
        {
            var v = vMin + (vMax - vMin) * i / 4;
            var y = Y(v);
            yTicks.Append($"<line x1='{ML - 4}' y1='{y:F1}' x2='{ML}' y2='{y:F1}' stroke='#999'/>");
            yTicks.Append($"<text x='{ML - 8}' y='{y + 4:F1}' text-anchor='end' font-size='11' fill='#666'>{v:G6}</text>");
        }
        // X 轴首/中/尾 3 个时间刻度
        var xTicks = new StringBuilder();
        foreach (var t in new[] { pts[0].T, pts[pts.Count / 2].T, pts[^1].T })
        {
            xTicks.Append($"<line x1='{X(t):F1}' y1='{MT + ph}' x2='{X(t):F1}' y2='{MT + ph + 4}' stroke='#999'/>");
            xTicks.Append($"<text x='{X(t):F1}' y='{MT + ph + 18}' text-anchor='middle' font-size='11' fill='#666'>{t:HH:mm:ss}</text>");
        }

        return $@"
<div style='margin-top:10px'><b style='font-size:13px'>{CsvHtml(property)}</b></div>
<svg class='chart' width='{W}' height='{H}' viewBox='0 0 {W} {H}' xmlns='http://www.w3.org/2000/svg'>
<rect x='{ML}' y='{MT}' width='{pw}' height='{ph}' fill='white' stroke='#ddd'/>
<polygon points='{area}' fill='rgba(0,102,204,0.08)'/>
<line x1='{ML}' y1='{MT + ph}' x2='{ML + pw}' y2='{MT + ph}' stroke='#999'/>
<line x1='{ML}' y1='{MT}' x2='{ML}' y2='{MT + ph}' stroke='#999'/>
{yTicks}{xTicks}
<polyline points='{poly}' fill='none' stroke='#0066CC' stroke-width='1.5'/>
</svg>";
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string Csv(string? s)
    {
        if (s is null) return "";
        return s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static string CsvHtml(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
}
