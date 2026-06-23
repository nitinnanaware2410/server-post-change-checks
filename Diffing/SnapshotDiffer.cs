using System;
using System.Collections.Generic;
using System.Linq;
using PatchHealthCheck.Models;

namespace PatchHealthCheck.Diffing;

public static class SnapshotDiffer
{
    public static ServerDiffReport Compare(ServerSnapshot before, ServerSnapshot after)
    {
        var report = new ServerDiffReport
        {
            ServerName = before.ServerName,
            BeforeSuccess = before.Success,
            AfterSuccess = after.Success,
            BeforeError = before.ErrorMessage,
            AfterError = after.ErrorMessage
        };

        if (!before.Success || !after.Success)
            return report; // nothing meaningful to diff if a capture failed

        report.Services = DiffServices(before.Services, after.Services);
        report.Processes = DiffProcesses(before.Processes, after.Processes);
        report.LoggedOnUsers = DiffUsers(before.LoggedOnUsers, after.LoggedOnUsers);
        report.Hotfixes = DiffHotfixes(before.Hotfixes, after.Hotfixes);
        report.ScheduledTasks = DiffTasks(before.ScheduledTasks, after.ScheduledTasks);
        report.ErrorEvents = DiffEvents(after.RecentErrorEvents, before.CapturedAtUtc);
        report.Os = DiffOs(before, after);

        return report;
    }

    private static CategoryDiff DiffServices(List<ServiceInfo> b, List<ServiceInfo> a)
    {
        var diff = new CategoryDiff { CategoryName = "Services" };
        var bMap = b.ToDictionary(x => x.Key, x => x);
        var aMap = a.ToDictionary(x => x.Key, x => x);

        foreach (var key in aMap.Keys.Except(bMap.Keys))
            diff.Items.Add(new DiffItem { Kind = DiffKind.Added, Key = key, After = $"{aMap[key].State} ({aMap[key].StartMode})", Detail = aMap[key].DisplayName });

        foreach (var key in bMap.Keys.Except(aMap.Keys))
            diff.Items.Add(new DiffItem { Kind = DiffKind.Removed, Key = key, Before = $"{bMap[key].State} ({bMap[key].StartMode})", Detail = bMap[key].DisplayName });

        foreach (var key in bMap.Keys.Intersect(aMap.Keys))
        {
            var bs = bMap[key]; var as_ = aMap[key];
            if (bs.State != as_.State || bs.StartMode != as_.StartMode)
            {
                diff.Items.Add(new DiffItem
                {
                    Kind = DiffKind.Changed,
                    Key = key,
                    Before = $"{bs.State} ({bs.StartMode})",
                    After = $"{as_.State} ({as_.StartMode})",
                    Detail = bs.DisplayName
                });
            }
        }
        return diff;
    }

    private static CategoryDiff DiffProcesses(List<ProcessInfo> b, List<ProcessInfo> a)
    {
        var diff = new CategoryDiff { CategoryName = "Processes / Apps" };
        var bMap = b.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Count());
        var aMap = a.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Count());

        foreach (var key in aMap.Keys.Except(bMap.Keys))
            diff.Items.Add(new DiffItem { Kind = DiffKind.Added, Key = key, After = $"{aMap[key]} instance(s)" });

        foreach (var key in bMap.Keys.Except(aMap.Keys))
            diff.Items.Add(new DiffItem { Kind = DiffKind.Removed, Key = key, Before = $"{bMap[key]} instance(s)" });

        foreach (var key in bMap.Keys.Intersect(aMap.Keys))
        {
            if (bMap[key] != aMap[key])
                diff.Items.Add(new DiffItem { Kind = DiffKind.Changed, Key = key, Before = $"{bMap[key]} instance(s)", After = $"{aMap[key]} instance(s)" });
        }
        return diff;
    }

    private static CategoryDiff DiffUsers(List<LoggedOnUser> b, List<LoggedOnUser> a)
    {
        var diff = new CategoryDiff { CategoryName = "Logged-on Users" };
        var bSet = b.Select(x => x.Key).ToHashSet();
        var aSet = a.Select(x => x.Key).ToHashSet();

        foreach (var key in aSet.Except(bSet))
            diff.Items.Add(new DiffItem { Kind = DiffKind.Added, Key = key, After = "Logged in" });
        foreach (var key in bSet.Except(aSet))
            diff.Items.Add(new DiffItem { Kind = DiffKind.Removed, Key = key, Before = "Logged in" });
        return diff;
    }

    private static CategoryDiff DiffHotfixes(List<HotfixInfo> b, List<HotfixInfo> a)
    {
        var diff = new CategoryDiff { CategoryName = "Installed Hotfixes" };
        var bMap = b.ToDictionary(x => x.Key, x => x);
        var aMap = a.ToDictionary(x => x.Key, x => x);

        foreach (var key in aMap.Keys.Except(bMap.Keys))
            diff.Items.Add(new DiffItem { Kind = DiffKind.Added, Key = key, After = aMap[key].Description, Detail = aMap[key].InstalledOn?.ToShortDateString() ?? "" });
        // Removed hotfixes are unusual but worth flagging (e.g. uninstall-on-failure)
        foreach (var key in bMap.Keys.Except(aMap.Keys))
            diff.Items.Add(new DiffItem { Kind = DiffKind.Removed, Key = key, Before = bMap[key].Description });
        return diff;
    }

    private static CategoryDiff DiffTasks(List<ScheduledTaskInfo> b, List<ScheduledTaskInfo> a)
    {
        var diff = new CategoryDiff { CategoryName = "Scheduled Tasks" };
        var bMap = b.ToDictionary(x => x.Key, x => x);
        var aMap = a.ToDictionary(x => x.Key, x => x);

        foreach (var key in aMap.Keys.Except(bMap.Keys))
            diff.Items.Add(new DiffItem { Kind = DiffKind.Added, Key = key, After = aMap[key].State });
        foreach (var key in bMap.Keys.Except(aMap.Keys))
            diff.Items.Add(new DiffItem { Kind = DiffKind.Removed, Key = key, Before = bMap[key].State });
        foreach (var key in bMap.Keys.Intersect(aMap.Keys))
        {
            if (bMap[key].State != aMap[key].State)
                diff.Items.Add(new DiffItem { Kind = DiffKind.Changed, Key = key, Before = bMap[key].State, After = aMap[key].State });
        }
        return diff;
    }

    private static CategoryDiff DiffEvents(List<EventLogEntryInfo> afterEvents, DateTime beforeCaptureUtc)
    {
        // Only events that occurred after the "Before" snapshot are genuinely new since patching.
        var diff = new CategoryDiff { CategoryName = "New Error Events" };
        foreach (var ev in afterEvents.Where(e => e.TimeGenerated.ToUniversalTime() > beforeCaptureUtc).OrderByDescending(e => e.TimeGenerated))
        {
            diff.Items.Add(new DiffItem
            {
                Kind = DiffKind.Added,
                Key = $"{ev.LogFile}/{ev.SourceName}/{ev.EventCode}",
                After = ev.TimeGenerated.ToString("g"),
                Detail = ev.Message
            });
        }
        return diff;
    }

    private static CategoryDiff DiffOs(ServerSnapshot before, ServerSnapshot after)
    {
        var diff = new CategoryDiff { CategoryName = "OS / Reboot" };

        diff.Items.Add(new DiffItem
        {
            Kind = DiffKind.Changed,
            IsInformational = true,
            Key = "Uptime at capture",
            Before = FormatUptime(before.Os.Uptime),
            After = FormatUptime(after.Os.Uptime),
            Detail = "Current uptime at each capture time"
        });

        bool rebooted = after.Os.LastBootUpTime != before.Os.LastBootUpTime && after.Os.LastBootUpTime > before.Os.LastBootUpTime;

        if (after.Os.LastBootUpTime != before.Os.LastBootUpTime)
        {
            diff.Items.Add(new DiffItem
            {
                Kind = DiffKind.Changed,
                Key = "Last Boot Time",
                Before = before.Os.LastBootUpTime.ToString("g"),
                After = after.Os.LastBootUpTime.ToString("g"),
                Detail = rebooted ? "Server rebooted between captures" : ""
            });
        }

        if (rebooted)
        {
            var ev = after.Reboot;
            diff.Items.Add(new DiffItem
            {
                Kind = ev.RebootType == "Unexpected" ? DiffKind.Warning : DiffKind.Changed,
                IsInformational = ev.RebootType != "Unexpected",
                Key = "Reboot Verified (Event Log)",
                Before = "",
                After = ev.BootEventFound ? $"{ev.RebootType}{(ev.MatchesReportedUptime ? "" : " (time mismatch vs uptime)")}" : "Not confirmed in event log",
                Detail = ev.Detail
            });
        }
        else
        {
            diff.Items.Add(new DiffItem
            {
                Kind = DiffKind.Changed,
                IsInformational = true,
                Key = "Reboot Verified (Event Log)",
                After = "No reboot detected between captures",
                Detail = "Uptime did not reset — server was not restarted during this window"
            });
        }

        var health = after.Health;
        diff.Items.Add(new DiffItem
        {
            Kind = health.Warnings.Count > 0 ? DiffKind.Warning : DiffKind.Changed,
            IsInformational = health.Warnings.Count == 0,
            Key = "System Health (After) — heuristic, not definitive",
            After = health.Status,
            Detail = health.Warnings.Count > 0
                ? string.Join("; ", health.Warnings)
                : $"CPU {health.CpuLoadPercent:F0}%, Free mem {health.FreeMemoryPercent:F0}%, Queue {health.ProcessorQueueLength} (CPUs={health.LogicalProcessors})"
        });

        if (before.PendingReboot != after.PendingReboot || (after.PendingReboot && after.PendingRebootReasons.Count > 0))
        {
            diff.Items.Add(new DiffItem
            {
                Kind = after.PendingReboot ? DiffKind.Added : DiffKind.Removed,
                Key = "Pending Reboot",
                Before = before.PendingReboot ? "Yes" : "No",
                After = after.PendingReboot ? "Yes" : "No",
                Detail = string.Join(", ", after.PendingRebootReasons)
            });
        }

        if (before.Os.BuildNumber != after.Os.BuildNumber)
        {
            diff.Items.Add(new DiffItem
            {
                Kind = DiffKind.Changed,
                Key = "OS Build Number",
                Before = before.Os.BuildNumber,
                After = after.Os.BuildNumber
            });
        }

        return diff;
    }

    private static string FormatUptime(TimeSpan ts) => $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
}
