using System;
using System.Collections.Generic;

namespace PatchHealthCheck.Models;

public class ServerSnapshot
{
    public string ServerName { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; }
    public string Label { get; set; } = ""; // "Before" or "After"
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = "";

    public OsInfo Os { get; set; } = new();
    public List<ServiceInfo> Services { get; set; } = new();
    public List<ProcessInfo> Processes { get; set; } = new();
    public List<LoggedOnUser> LoggedOnUsers { get; set; } = new();
    public List<HotfixInfo> Hotfixes { get; set; } = new();
    public List<ScheduledTaskInfo> ScheduledTasks { get; set; } = new();
    public List<EventLogEntryInfo> RecentErrorEvents { get; set; } = new();
    public bool PendingReboot { get; set; }
    public List<string> PendingRebootReasons { get; set; } = new();
    public RebootEvidence Reboot { get; set; } = new();
    public HealthSnapshot Health { get; set; } = new();
}

public class RebootEvidence
{
    public bool BootEventFound { get; set; }
    public DateTime? BootEventTime { get; set; }
    public bool MatchesReportedUptime { get; set; }
    public string RebootType { get; set; } = "Unknown"; // Planned, Unexpected, Unknown
    public string Detail { get; set; } = "";
}

public class HealthSnapshot
{
    public double CpuLoadPercent { get; set; } = -1;
    public double FreeMemoryPercent { get; set; } = -1;
    public long ProcessorQueueLength { get; set; } = -1;
    public int LogicalProcessors { get; set; }
    public string Status { get; set; } = "Unknown"; // Healthy, Elevated, Unknown
    public List<string> Warnings { get; set; } = new();
}

public class OsInfo
{
    public string Caption { get; set; } = "";
    public string Version { get; set; } = "";
    public string BuildNumber { get; set; } = "";
    public DateTime LastBootUpTime { get; set; }
    public string Architecture { get; set; } = "";
    public TimeSpan Uptime { get; set; }
}

public class ServiceInfo
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string State { get; set; } = "";       // Running/Stopped
    public string StartMode { get; set; } = "";   // Auto/Manual/Disabled
    public string PathName { get; set; } = "";

    public string Key => Name;
}

public class ProcessInfo
{
    public string Name { get; set; } = "";
    public int ProcessId { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string Owner { get; set; } = "";
    public DateTime? CreationDate { get; set; }

    // Grouped key for diffing (process name + path), since PIDs change every run
    public string Key => $"{Name}|{ExecutablePath}";
}

public class LoggedOnUser
{
    public string UserName { get; set; } = "";
    public string Domain { get; set; } = "";
    public string LogonType { get; set; } = "";
    public DateTime? LogonTime { get; set; }

    public string Key => $"{Domain}\\{UserName}";
}

public class HotfixInfo
{
    public string HotFixId { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime? InstalledOn { get; set; }

    public string Key => HotFixId;
}

public class ScheduledTaskInfo
{
    public string TaskName { get; set; } = "";
    public string State { get; set; } = "";
    public string Path { get; set; } = "";

    public string Key => Path + TaskName;
}

public class EventLogEntryInfo
{
    public string LogFile { get; set; } = "";
    public string SourceName { get; set; } = "";
    public int EventCode { get; set; }
    public string Message { get; set; } = "";
    public DateTime TimeGenerated { get; set; }

    public string Key => $"{LogFile}|{SourceName}|{EventCode}|{TimeGenerated:o}";
}
