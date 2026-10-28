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

    /// <summary>导出全部格式，返回已生成的文件路径列表。</summary>
    public IReadOnlyList<string> ExportAll(
        string taskName, Guid taskId, MonitorTaskConfig config,
        IReadOnlyList<SampleRecord> records, DateTime startedAt, DateTime endedAt)
    {
        var dir = Path.Combine(_outputRoot, $"{Sanitize(taskName)}_{DateTime.Now:yyyyMMdd_HHmmss}");
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
        var sb = new StringBuilder("时间戳,节点ID,节点路径,属性,值\n");
        foreach (var r in records)
            sb.AppendLine($"{r.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{r.NodeId},{Csv(r.NodePath)},{Csv(r.Property)},{Csv(r.Value)}");
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
th{{background:#f0f0f0}} .meta{{color:#666;font-size:13px}}</style></head>
<body>
<h1>监控报告：{CsvHtml(name)}</h1>
<p class=""meta"">任务ID: {id:N} | 开始: {startedAt:yyyy-MM-dd HH:mm:ss} | 结束: {endedAt:yyyy-MM-dd HH:mm:ss} | 记录数: {records.Count}</p>
<h2>配置摘要</h2>
<table><tr><th>目标</th><td>{CsvHtml(config.Target.ToString())}</td></tr>
<tr><th>属性</th><td>{string.Join(", ", config.Properties)}</td></tr>
<tr><th>过滤</th><td>{CsvHtml(config.Filter.Rules.Count > 0 ? string.Join("; ", config.Filter.Rules.Select(r => r.Property + " ~ " + r.Pattern)) : "无")}</td></tr>
<tr><th>触发器</th><td>{CsvHtml(config.Trigger.Kind)}</td></tr>
<tr><th>时间器</th><td>{CsvHtml(config.TimeBox.Boxes.Count > 0 ? string.Join("; ", config.TimeBox.Boxes.Select(b => b.Kind)) : "无")}</td></tr></table>
<h2>属性变化摘要</h2>
<table><tr><th>属性</th><th>条数</th><th>值变化</th><th>时间范围</th></tr>{rows}</table>
</body></html>";

        File.WriteAllText(path, html, new UTF8Encoding(true));
        return path;
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
