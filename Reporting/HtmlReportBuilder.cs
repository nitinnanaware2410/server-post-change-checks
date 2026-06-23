using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using PatchHealthCheck.Diffing;

namespace PatchHealthCheck.Reporting;

public static class HtmlReportBuilder
{
    public static string Build(List<ServerDiffReport> reports, DateTime beforeUtc, DateTime afterUtc)
    {
        var sb = new StringBuilder();
        sb.Append($@"<!DOCTYPE html>
<html><head><meta charset='utf-8'>
<title>Patch Health Check Report</title>
<style>
{Css}
</style></head><body>
<div class='wrap'>
<h1>Patch Health Check Report</h1>
<div class='meta'>
  <span><b>Before:</b> {beforeUtc.ToLocalTime():g}</span>
  <span><b>After:</b> {afterUtc.ToLocalTime():g}</span>
  <span><b>Generated:</b> {DateTime.Now:g}</span>
  <span><b>Servers:</b> {reports.Count}</span>
</div>
<div class='summary-grid'>");

        foreach (var r in reports.OrderByDescending(r => r.TotalChanges))
        {
            string status = !r.BeforeSuccess || !r.AfterSuccess ? "error" : r.TotalChanges == 0 ? "clean" : "changed";
            string statusLabel = status == "error" ? "Capture Failed" : status == "clean" ? "No Changes" : $"{r.TotalChanges} change(s)";
            sb.Append($@"<a href='#srv-{Esc(r.ServerName)}' class='summary-card {status}'>
<div class='srv-name'>{Esc(r.ServerName)}</div>
<div class='srv-status'>{statusLabel}</div>
</a>");
        }
        sb.Append("</div>");

        foreach (var r in reports)
        {
            sb.Append($"<div class='server-section' id='srv-{Esc(r.ServerName)}'>");
            sb.Append($"<h2>{Esc(r.ServerName)}</h2>");

            if (!r.BeforeSuccess || !r.AfterSuccess)
            {
                sb.Append("<div class='card error-card'>");
                if (!r.BeforeSuccess) sb.Append($"<p><b>Before capture failed:</b> {Esc(r.BeforeError)}</p>");
                if (!r.AfterSuccess) sb.Append($"<p><b>After capture failed:</b> {Esc(r.AfterError)}</p>");
                sb.Append("</div></div>");
                continue;
            }

            // OS/Uptime always renders — it carries the informational uptime-at-capture row even when nothing else changed.
            AppendCategoryTable(sb, r.Os);

            if (r.TotalChanges == 0)
            {
                sb.Append("<div class='card clean-card'>No other differences detected — system looks consistent with the pre-patch baseline.</div>");
                sb.Append("</div>");
                continue;
            }

            foreach (var cat in new[] { r.Services, r.Processes, r.LoggedOnUsers, r.Hotfixes, r.ScheduledTasks, r.ErrorEvents })
            {
                if (!cat.HasChanges) continue;
                AppendCategoryTable(sb, cat);
            }

            sb.Append("</div>");
        }

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static void AppendCategoryTable(StringBuilder sb, CategoryDiff cat)
    {
        if (cat.Items.Count == 0) return;
        sb.Append($"<div class='category'><h3>{Esc(cat.CategoryName)} <span class='count'>{cat.Items.Count}</span></h3>");
        sb.Append("<table><thead><tr><th>Item</th><th>Change</th><th>Before</th><th>After</th><th>Detail</th></tr></thead><tbody>");
        foreach (var item in cat.Items)
        {
            string rowClass = item.Kind == DiffKind.Warning ? "row-warning"
                : item.IsInformational ? "row-info"
                : item.Kind switch
                {
                    DiffKind.Added => "row-added",
                    DiffKind.Removed => "row-removed",
                    DiffKind.Changed => "row-changed",
                    _ => ""
                };
            string changeLabel = item.IsInformational ? "Info" : item.Kind.ToString();
            sb.Append($"<tr class='{rowClass}'><td>{Esc(item.Key)}</td><td>{changeLabel}</td><td>{Esc(item.Before)}</td><td>{Esc(item.After)}</td><td>{Esc(item.Detail)}</td></tr>");
        }
        sb.Append("</tbody></table></div>");
    }

    private static string Esc(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Css = @"
body { font-family: Segoe UI, Arial, sans-serif; background:#f4f6f8; color:#1f2937; margin:0; }
.wrap { max-width: 1200px; margin: 0 auto; padding: 24px; }
h1 { margin-bottom: 4px; }
.meta { display:flex; gap:24px; flex-wrap:wrap; color:#4b5563; margin-bottom:20px; font-size:14px; }
.summary-grid { display:flex; flex-wrap:wrap; gap:12px; margin-bottom:32px; }
.summary-card { display:block; text-decoration:none; min-width:160px; padding:14px 16px; border-radius:8px; box-shadow:0 1px 3px rgba(0,0,0,.1); }
.summary-card.clean { background:#e8f5e9; color:#1b5e20; }
.summary-card.changed { background:#fff3e0; color:#7a4a00; }
.summary-card.error { background:#fdecea; color:#7f1d1d; }
.srv-name { font-weight:600; font-size:15px; }
.srv-status { font-size:13px; margin-top:4px; }
.server-section { background:#fff; border-radius:10px; padding:20px 24px; margin-bottom:24px; box-shadow:0 1px 4px rgba(0,0,0,.08); }
.server-section h2 { margin-top:0; border-bottom:2px solid #e5e7eb; padding-bottom:8px; }
.category { margin-top:18px; }
.category h3 { margin-bottom:8px; font-size:15px; }
.count { background:#e5e7eb; border-radius:10px; padding:1px 9px; font-size:12px; color:#374151; }
table { width:100%; border-collapse: collapse; font-size: 13px; }
th, td { text-align:left; padding:6px 10px; border-bottom:1px solid #eef0f2; }
th { background:#f9fafb; font-weight:600; color:#374151; }
tr.row-added td { background:#eafaf1; }
tr.row-removed td { background:#fdecea; }
tr.row-changed td { background:#fff7e6; }
tr.row-info td { background:#f3f4f6; color:#4b5563; }
tr.row-warning td { background:#fdecea; color:#7f1d1d; font-weight:600; }
.card { padding:14px 16px; border-radius:8px; }
.clean-card { background:#e8f5e9; color:#1b5e20; }
.error-card { background:#fdecea; color:#7f1d1d; }
";
}
