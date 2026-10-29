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
    /// <param name="chartSourcePath">可选：已渲染好的走势图 PNG，将复制为导出目录下 chart.png 并在报告中引用。</param>
    public IReadOnlyList<string> ExportAll(
        string taskName, Guid taskId, MonitorTaskConfig config,
        IReadOnlyList<SampleRecord> records, DateTime startedAt, DateTime endedAt,
        string? chartSourcePath = null)
    {
        var dayDir = Path.Combine(_outputRoot, DateTime.Now.ToString("yyyyMMdd"));
        var dir = Path.Combine(dayDir, $"{Sanitize(taskName)}_{DateTime.Now:HHmmss}");
        Directory.CreateDirectory(dir);

        var files = new List<string>
        {
            ExportCsv(dir, config, records),
            ExportJson(dir, taskName, taskId, config, records, startedAt, endedAt),
            ExportHtml(dir, taskName, taskId, config, records, startedAt, endedAt),
        };
        if (chartSourcePath is not null)
        {
            var chartPath = Path.Combine(dir, "chart.png");
            File.Copy(chartSourcePath, chartPath, overwrite: true);
            files.Add(chartPath);
        }
        return files;
    }

    /// <summary>导出与实时落盘同格式的行式 CSV：监控对象,值1..值N,时间戳。</summary>
    private static string ExportCsv(string dir, MonitorTaskConfig config, IReadOnlyList<SampleRecord> records)
    {
        var path = Path.Combine(dir, "data.csv");
        var n = config.Extractors.Count > 0 ? config.Extractors.Count : 1;
        var target = string.Join(";", config.Properties);

        var sb = new StringBuilder();
        var heads = new List<string> { "监控对象" };
        for (int i = 1; i <= n; i++) heads.Add($"值{i}");
        heads.Add("时间戳");
        sb.AppendLine(string.Join(",", heads));

        for (int i = 0; i + n <= records.Count; i += n)
        {
            var ts = records[i].Timestamp;
            var vals = records.Skip(i).Take(n).Select(r => Csv(r.Value));
            sb.AppendLine($"{Csv(target)},{string.Join(",", vals)},{ts:yyyy-MM-dd HH:mm:ss.fff}");
        }
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
.chart{{border:1px solid #e0e0e0;background:#fcfcfc;margin-top:8px}}
.lg{{display:inline-block;margin:2px 14px 2px 0;padding:2px 10px;border:1px solid #ddd;border-radius:3px;cursor:pointer;font-size:13px;background:#fff;user-select:none}}
.lg i{{display:inline-block;width:18px;height:4px;margin-right:6px;vertical-align:middle}}
.lg.off{{opacity:.4;text-decoration:line-through}}
.lg.disabled{{cursor:not-allowed;color:#999}}</style></head>
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
{BuildInteractiveChart(config, records)}
<h2>属性变化摘要</h2>
<table><tr><th>属性</th><th>条数</th><th>值变化</th><th>时间范围</th></tr>{rows}</table>
</body></html>";

        File.WriteAllText(path, html, new UTF8Encoding(true));
        return path;
    }

    /// <summary>
    /// 生成交互式走势图（原生 JS + Canvas，无外部依赖、离线可用）：
    /// 图例点击显示/隐藏值曲线，Y 轴自动适配可见曲线范围，窗口缩放自动重绘；非数值列图例置灰。
    /// </summary>
    private static string BuildInteractiveChart(MonitorTaskConfig config, IReadOnlyList<SampleRecord> records)
    {
        var n = config.Extractors.Count > 0 ? config.Extractors.Count : 1;

        // 与 CSV 同规则切行：(时间戳, 值1..值N)
        var rows = new List<(DateTime Ts, string?[] Vals)>();
        for (int i = 0; i + n <= records.Count; i += n)
        {
            var vals = new string?[n];
            for (int c = 0; c < n; c++) vals[c] = records[i + c].Value;
            rows.Add((records[i].Timestamp, vals));
        }

        // 每列：可解析数值点 [epoch毫秒, 值]；无数值点的列 numeric=false（图例置灰）
        var series = new List<object>();
        for (int c = 0; c < n; c++)
        {
            var pts = rows
                .Select(r => (r.Ts, V: Parsed(r.Vals[c])))
                .Where(p => p.V.HasValue)
                .Select(p => (object)new[] { new DateTimeOffset(p.Ts).ToUnixTimeMilliseconds(), p.V!.Value })
                .ToList();
            series.Add(new { name = $"值{c + 1}", numeric = pts.Count > 0, points = pts });
        }

        var json = System.Text.Json.JsonSerializer.Serialize(series);
        return "<div id='chartWrap' style='margin-top:8px'>" +
               "<div id='chartLegend'></div>" +
               "<canvas id='chartCanvas' style='display:block;border:1px solid #e0e0e0;background:#fcfcfc'></canvas>" +
               "<p class='meta'>点击图例可显示/隐藏对应曲线，纵轴将自动适配可见曲线的数值范围。</p>" +
               InteractiveChartScript.Replace("__SERIES_JSON__", json) +
               "</div>";
    }

    private static double? Parsed(string? s)
        => double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v
         : (double.TryParse(s, out var v2) ? v2 : null);

    /// <summary>交互图脚本（占位符 __SERIES_JSON__ 由生成时替换；verbatim 字符串避免 JS 大括号转义）。</summary>
    private const string InteractiveChartScript = @"
<script>
(function () {
  var SERIES = __SERIES_JSON__;
  var wrap = document.getElementById('chartWrap');
  if (!SERIES || SERIES.length === 0) {
    wrap.innerHTML = '<p class=""meta"">无数值型采样数据，未生成走势图。</p>';
    return;
  }
  var colors = ['#1F77B4', '#D62728', '#2CA02C', '#FF7F0E', '#9467BD', '#8C564B', '#00899C', '#E377C2'];
  var visible = SERIES.map(function (s) { return s.numeric; });
  var legend = document.getElementById('chartLegend');
  var canvas = document.getElementById('chartCanvas');

  SERIES.forEach(function (s, i) {
    var item = document.createElement('span');
    item.className = 'lg' + (s.numeric ? '' : ' off disabled');
    item.innerHTML = '<i style=""background:' + colors[i % colors.length] + '""></i>' + s.name;
    item.title = s.numeric ? '点击显示/隐藏该曲线' : '非数值列，不可绘制';
    if (s.numeric) {
      item.onclick = function () {
        visible[i] = !visible[i];
        item.className = 'lg' + (visible[i] ? '' : ' off');
        render();
      };
    }
    legend.appendChild(item);
  });

  function fmt(v) {
    var a = Math.abs(v);
    if (a >= 1e6 || (a > 0 && a <= 1e-4)) return v.toExponential(2);
    return parseFloat(v.toFixed(4)).toString();
  }
  function hms(ms) {
    var d = new Date(ms);
    function p(n) { return n < 10 ? '0' + n : '' + n; }
    return p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
  }

  function render() {
    var dpr = window.devicePixelRatio || 1;
    var W = Math.max(320, wrap.clientWidth - 2), H = 360;
    canvas.width = W * dpr; canvas.height = H * dpr;
    canvas.style.width = W + 'px'; canvas.style.height = H + 'px';
    var ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, W, H);
    ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, W, H);

    var L = 86, R = 18, T = 18, B = 44;
    var pw = W - L - R, ph = H - T - B;

    var shown = [];
    SERIES.forEach(function (s, i) { if (visible[i] && s.points.length > 0) shown.push(s); });
    if (shown.length === 0) {
      ctx.fillStyle = '#666'; ctx.font = '13px Segoe UI, Microsoft YaHei';
      ctx.textAlign = 'center';
      ctx.fillText('未选择任何曲线（点击上方图例显示）', W / 2, H / 2);
      return;
    }

    // Y 轴自动适配：可见系列的合并范围 + 5% 余量（恒值时上下各留 1）
    var tMin = Infinity, tMax = -Infinity, vMin = Infinity, vMax = -Infinity;
    shown.forEach(function (s) {
      s.points.forEach(function (p) {
        if (p[0] < tMin) tMin = p[0];
        if (p[0] > tMax) tMax = p[0];
        if (p[1] < vMin) vMin = p[1];
        if (p[1] > vMax) vMax = p[1];
      });
    });
    var pad = (vMax - vMin) * 0.05; if (pad <= 0) pad = 1;
    vMin -= pad; vMax += pad;
    var tSpan = Math.max(1, tMax - tMin);

    function X(t) { return L + (t - tMin) / tSpan * pw; }
    function Y(v) { return T + (vMax - v) / (vMax - vMin) * ph; }

    ctx.strokeStyle = '#E1E4E8'; ctx.lineWidth = 1;
    ctx.fillStyle = '#3C3C3C'; ctx.font = '11px Segoe UI, Microsoft YaHei';
    ctx.textAlign = 'right';
    for (var i = 0; i <= 5; i++) {
      var v = vMin + (vMax - vMin) * i / 5, y = Y(v);
      ctx.beginPath(); ctx.moveTo(L, y); ctx.lineTo(L + pw, y); ctx.stroke();
      ctx.fillText(fmt(v), L - 8, y + 4);
    }
    ctx.textAlign = 'center';
    [tMin, (tMin + tMax) / 2, tMax].forEach(function (t) {
      var x = X(t);
      ctx.beginPath(); ctx.moveTo(x, T); ctx.lineTo(x, T + ph); ctx.stroke();
      ctx.fillText(hms(t), x, T + ph + 16);
    });
    ctx.strokeStyle = '#5A5A5A'; ctx.lineWidth = 1.5;
    ctx.strokeRect(L, T, pw, ph);

    shown.forEach(function (s) {
      var idx = SERIES.indexOf(s);
      var c = colors[idx % colors.length];
      if (s.points.length === 1) {
        ctx.fillStyle = c;
        ctx.beginPath(); ctx.arc(X(s.points[0][0]), Y(s.points[0][1]), 3.5, 0, Math.PI * 2); ctx.fill();
        return;
      }
      ctx.strokeStyle = c; ctx.lineWidth = 2; ctx.lineJoin = 'round';
      ctx.beginPath();
      s.points.forEach(function (p, i) {
        var x = X(p[0]), y = Y(p[1]);
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      });
      ctx.stroke();
    });
  }
  render();
  window.addEventListener('resize', render);
})();
</script>";

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
