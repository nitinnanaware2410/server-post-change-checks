using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Security;
using PatchHealthCheck.Models;

namespace PatchHealthCheck.Collectors;

/// <summary>
/// Collects a point-in-time snapshot from a server using WMI/CIM only (no WinRM, no agent).
/// </summary>
public static class CimCollector
{
    public static ServerSnapshot Collect(string serverName, string label, string? domainUser, SecureString? password, int maxEventEntries = 50)
    {
        var snap = new ServerSnapshot
        {
            ServerName = serverName,
            Label = label,
            CapturedAtUtc = DateTime.UtcNow
        };

        ConnectionOptions options = BuildConnectionOptions(domainUser, password);

        try
        {
            var scope = new ManagementScope($@"\\{serverName}\root\cimv2", options);
            scope.Connect();

            snap.Os = GetOsInfo(scope);
            snap.Services = GetServices(scope);
            snap.Processes = GetProcesses(scope);
            snap.LoggedOnUsers = GetLoggedOnUsers(scope);
            snap.Hotfixes = GetHotfixes(scope);
            snap.ScheduledTasks = GetScheduledTasks(serverName, options);
            snap.RecentErrorEvents = GetRecentErrorEvents(scope, snap.Os.LastBootUpTime, maxEventEntries);
            (snap.PendingReboot, snap.PendingRebootReasons) = GetPendingReboot(serverName, options);
            snap.Reboot = GetRebootEvidence(scope, snap.Os.LastBootUpTime);
            snap.Health = GetHealthSnapshot(scope, snap.Services);

            snap.Success = true;
        }
        catch (Exception ex)
        {
            snap.Success = false;
            snap.ErrorMessage = ex.Message;
        }

        return snap;
    }

    private static ConnectionOptions BuildConnectionOptions(string? domainUser, SecureString? password)
    {
        var options = new ConnectionOptions
        {
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,
            EnablePrivileges = true,
            Timeout = TimeSpan.FromSeconds(30)
        };

        if (!string.IsNullOrWhiteSpace(domainUser))
        {
            options.Username = domainUser;
            options.SecurePassword = password;
        }

        return options;
    }

    private static OsInfo GetOsInfo(ManagementScope scope)
    {
        var os = new OsInfo();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Caption, Version, BuildNumber, LastBootUpTime, OSArchitecture FROM Win32_OperatingSystem"));
        foreach (ManagementObject mo in searcher.Get())
        {
            os.Caption = mo["Caption"]?.ToString() ?? "";
            os.Version = mo["Version"]?.ToString() ?? "";
            os.BuildNumber = mo["BuildNumber"]?.ToString() ?? "";
            os.Architecture = mo["OSArchitecture"]?.ToString() ?? "";
            var lastBoot = mo["LastBootUpTime"]?.ToString();
            if (!string.IsNullOrEmpty(lastBoot))
                os.LastBootUpTime = ManagementDateTimeConverter.ToDateTime(lastBoot);
        }
        if (os.LastBootUpTime != default)
            os.Uptime = DateTime.Now - os.LastBootUpTime;
        return os;
    }

    private static List<ServiceInfo> GetServices(ManagementScope scope)
    {
        var list = new List<ServiceInfo>();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, DisplayName, State, StartMode, PathName FROM Win32_Service"));
        foreach (ManagementObject mo in searcher.Get())
        {
            list.Add(new ServiceInfo
            {
                Name = mo["Name"]?.ToString() ?? "",
                DisplayName = mo["DisplayName"]?.ToString() ?? "",
                State = mo["State"]?.ToString() ?? "",
                StartMode = mo["StartMode"]?.ToString() ?? "",
                PathName = mo["PathName"]?.ToString() ?? ""
            });
        }
        return list;
    }

    private static List<ProcessInfo> GetProcesses(ManagementScope scope)
    {
        var list = new List<ProcessInfo>();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, ProcessId, ExecutablePath, CreationDate FROM Win32_Process"));
        foreach (ManagementObject mo in searcher.Get())
        {
            string owner = "";
            try
            {
                var outParams = mo.InvokeMethod("GetOwner", null, null);
                owner = outParams?.Properties["User"]?.Value?.ToString() ?? "";
            }
            catch { /* some system processes deny owner queries */ }

            DateTime? created = null;
            var cd = mo["CreationDate"]?.ToString();
            if (!string.IsNullOrEmpty(cd))
                created = ManagementDateTimeConverter.ToDateTime(cd);

            list.Add(new ProcessInfo
            {
                Name = mo["Name"]?.ToString() ?? "",
                ProcessId = Convert.ToInt32(mo["ProcessId"]),
                ExecutablePath = mo["ExecutablePath"]?.ToString() ?? "",
                Owner = owner,
                CreationDate = created
            });
        }
        return list;
    }

    private static List<LoggedOnUser> GetLoggedOnUsers(ManagementScope scope)
    {
        var list = new List<LoggedOnUser>();
        // Win32_LogonSession + Win32_LoggedOnUser association is noisy; Win32_ComputerSystem.UserName
        // plus interactive logon sessions (LogonType 2/10/11) gives a clean practical view.
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT Antecedent, Dependent FROM Win32_LoggedOnUser"));

        var seen = new HashSet<string>();
        foreach (ManagementObject mo in searcher.Get())
        {
            try
            {
                string antecedent = mo["Antecedent"]?.ToString() ?? "";
                // Format: \\.\root\cimv2:Win32_Account.Domain="X",Name="Y"
                string domain = ExtractQuoted(antecedent, "Domain");
                string name = ExtractQuoted(antecedent, "Name");
                if (string.IsNullOrEmpty(name)) continue;

                string key = $"{domain}\\{name}";
                if (!seen.Add(key)) continue;
                if (name.EndsWith("$")) continue; // machine accounts

                list.Add(new LoggedOnUser { UserName = name, Domain = domain, LogonType = "Interactive/Network" });
            }
            catch { }
        }
        return list;
    }

    private static string ExtractQuoted(string source, string field)
    {
        var marker = field + "=\"";
        var idx = source.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        var start = idx + marker.Length;
        var end = source.IndexOf('"', start);
        if (end < 0) return "";
        return source.Substring(start, end - start);
    }

    private static List<HotfixInfo> GetHotfixes(ManagementScope scope)
    {
        var list = new List<HotfixInfo>();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering"));
        foreach (ManagementObject mo in searcher.Get())
        {
            DateTime? installed = null;
            var raw = mo["InstalledOn"]?.ToString();
            if (!string.IsNullOrEmpty(raw) && DateTime.TryParse(raw, out var dt))
                installed = dt;

            list.Add(new HotfixInfo
            {
                HotFixId = mo["HotFixID"]?.ToString() ?? "",
                Description = mo["Description"]?.ToString() ?? "",
                InstalledOn = installed
            });
        }
        return list;
    }

    private static List<ScheduledTaskInfo> GetScheduledTasks(string serverName, ConnectionOptions options)
    {
        var list = new List<ScheduledTaskInfo>();
        try
        {
            var scope = new ManagementScope($@"\\{serverName}\root\Microsoft\Windows\TaskScheduler", options);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, State, Path FROM MSFT_ScheduledTask"));
            foreach (ManagementObject mo in searcher.Get())
            {
                var stateVal = mo["State"];
                list.Add(new ScheduledTaskInfo
                {
                    TaskName = mo["Name"]?.ToString() ?? "",
                    Path = mo["Path"]?.ToString() ?? "",
                    State = TranslateTaskState(stateVal)
                });
            }
        }
        catch
        {
            // Namespace not available on older OS or blocked — non-fatal, leave list empty.
        }
        return list;
    }

    private static string TranslateTaskState(object? stateVal)
    {
        if (stateVal == null) return "Unknown";
        int code = Convert.ToInt32(stateVal);
        return code switch
        {
            0 => "Unknown",
            1 => "Disabled",
            2 => "Queued",
            3 => "Ready",
            4 => "Running",
            _ => "Unknown"
        };
    }

    private static List<EventLogEntryInfo> GetRecentErrorEvents(ManagementScope scope, DateTime lastBootUtc, int maxEntries)
    {
        var list = new List<EventLogEntryInfo>();
        try
        {
            // EventType 1 = Error. Restrict to System/Application logs and since-last-boot to keep this fast.
            string wmiTime = ManagementDateTimeConverter.ToDmtfDateTime(lastBootUtc).Substring(0, 14) + ".000000+000";
            string query = $"SELECT LogFile, SourceName, EventCode, Message, TimeGenerated FROM Win32_NTLogEvent " +
                            $"WHERE (LogFile='System' OR LogFile='Application') AND EventType=1 AND TimeGenerated >= '{wmiTime}'";
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query))
            {
                Options = { Timeout = TimeSpan.FromSeconds(25) }
            };
            foreach (ManagementObject mo in searcher.Get())
            {
                if (list.Count >= maxEntries) break;
                var tg = mo["TimeGenerated"]?.ToString();
                DateTime when = !string.IsNullOrEmpty(tg) ? ManagementDateTimeConverter.ToDateTime(tg) : DateTime.MinValue;

                list.Add(new EventLogEntryInfo
                {
                    LogFile = mo["LogFile"]?.ToString() ?? "",
                    SourceName = mo["SourceName"]?.ToString() ?? "",
                    EventCode = mo["EventCode"] != null ? Convert.ToInt32(mo["EventCode"]) : 0,
                    Message = Truncate(mo["Message"]?.ToString() ?? "", 300),
                    TimeGenerated = when
                });
            }
        }
        catch
        {
            // Event log queries can be slow/blocked on some systems — non-fatal.
        }
        return list;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";

    private static (bool pending, List<string> reasons) GetPendingReboot(string serverName, ConnectionOptions options)
    {
        var reasons = new List<string>();
        try
        {
            var scope = new ManagementScope($@"\\{serverName}\root\default", options);
            scope.Connect();
            using var mc = new ManagementClass(scope, new ManagementPath("StdRegProv"), null);

            bool CheckKeyExists(uint hive, string path, string? valueName)
            {
                try
                {
                    var inParams = mc.GetMethodParameters("EnumKey");
                    inParams["hDefKey"] = hive;
                    inParams["sSubKeyName"] = path;
                    var result = mc.InvokeMethod("EnumKey", inParams, null);
                    var names = result?["sNames"] as string[];
                    return names != null && names.Length > 0;
                }
                catch { return false; }
            }

            const uint HKLM = 0x80000002;

            if (CheckKeyExists(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", null))
                reasons.Add("Component Based Servicing\\RebootPending");

            if (CheckKeyExists(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", null))
                reasons.Add("Windows Update\\RebootRequired");

            if (CheckKeyExists(HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations"))
            {
                // PendingFileRenameOperations is a value, not a subkey — checked via GetMultiStringValue instead.
            }

            try
            {
                var inParams = mc.GetMethodParameters("GetMultiStringValue");
                inParams["hDefKey"] = HKLM;
                inParams["sSubKeyName"] = @"SYSTEM\CurrentControlSet\Control\Session Manager";
                inParams["sValueName"] = "PendingFileRenameOperations";
                var result = mc.InvokeMethod("GetMultiStringValue", inParams, null);
                var values = result?["sValue"] as string[];
                if (values != null && values.Length > 0)
                    reasons.Add("PendingFileRenameOperations");
            }
            catch { }
        }
        catch
        {
            // If we can't check, don't claim a pending reboot.
        }

        return (reasons.Count > 0, reasons);
    }

    /// <summary>
    /// Best-effort classification of the most recent boot using System event log markers.
    /// 6005 = Event Log service started (fires right at boot). A 1074 (user/process-initiated
    /// restart) or 41/6008 (unexpected shutdown/power loss) shortly before it classifies the type.
    /// This is a heuristic, not an authoritative source — event logs can be cleared or rotated.
    /// </summary>
    private static RebootEvidence GetRebootEvidence(ManagementScope scope, DateTime lastBootUpTime)
    {
        var evidence = new RebootEvidence();
        if (lastBootUpTime == default) return evidence;

        try
        {
            string windowStart = ManagementDateTimeConverter.ToDmtfDateTime(lastBootUpTime.AddDays(-1)).Substring(0, 14) + ".000000+000";
            string query = "SELECT EventCode, TimeGenerated FROM Win32_NTLogEvent WHERE LogFile='System' AND " +
                           "(EventCode=6005 OR EventCode=6006 OR EventCode=6008 OR EventCode=1074 OR EventCode=41) AND " +
                           $"TimeGenerated >= '{windowStart}'";
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query))
            {
                Options = { Timeout = TimeSpan.FromSeconds(20) }
            };

            var events = new List<(int code, DateTime time)>();
            foreach (ManagementObject mo in searcher.Get())
            {
                var tg = mo["TimeGenerated"]?.ToString();
                if (string.IsNullOrEmpty(tg)) continue;
                events.Add((Convert.ToInt32(mo["EventCode"]), ManagementDateTimeConverter.ToDateTime(tg)));
            }

            // Find the 6005 (log service start) closest to the reported boot time.
            var bootEvent = events.Where(e => e.code == 6005)
                                   .OrderBy(e => Math.Abs((e.time - lastBootUpTime).TotalMinutes))
                                   .FirstOrDefault();

            if (bootEvent.time != default)
            {
                evidence.BootEventFound = true;
                evidence.BootEventTime = bootEvent.time;
                evidence.MatchesReportedUptime = Math.Abs((bootEvent.time - lastBootUpTime).TotalMinutes) <= 5;

                bool plannedNearby = events.Any(e => e.code == 1074 && Math.Abs((e.time - bootEvent.time).TotalMinutes) <= 15);
                bool unexpectedNearby = events.Any(e => (e.code == 41 || e.code == 6008) && Math.Abs((e.time - bootEvent.time).TotalMinutes) <= 15);

                if (unexpectedNearby)
                {
                    evidence.RebootType = "Unexpected";
                    evidence.Detail = "Crash/power-loss marker (EventID 41 or 6008) found near boot time";
                }
                else if (plannedNearby)
                {
                    evidence.RebootType = "Planned";
                    evidence.Detail = "User/process-initiated restart (EventID 1074) found near boot time";
                }
                else
                {
                    evidence.RebootType = "Unknown";
                    evidence.Detail = "Boot confirmed in event log, but no clear shutdown-type marker found nearby";
                }
            }
            else
            {
                evidence.Detail = "No matching boot event (6005) found in the System log for this window";
            }
        }
        catch (Exception ex)
        {
            evidence.Detail = $"Could not query event log for reboot evidence: {ex.Message}";
        }

        return evidence;
    }

    /// <summary>
    /// Best-effort "is this server struggling" check via WMI counters — not a definitive hang
    /// detector (a fully hung box typically stops answering WMI/RPC at all, which already shows
    /// up as a failed capture). This flags load levels worth a human look.
    /// </summary>
    private static HealthSnapshot GetHealthSnapshot(ManagementScope scope, List<ServiceInfo> services)
    {
        var health = new HealthSnapshot();
        try
        {
            using (var cpuSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT LoadPercentage, NumberOfLogicalProcessors FROM Win32_Processor")))
            {
                var loads = new List<double>();
                int logicalProcs = 0;
                foreach (ManagementObject mo in cpuSearcher.Get())
                {
                    if (mo["LoadPercentage"] != null) loads.Add(Convert.ToDouble(mo["LoadPercentage"]));
                    if (mo["NumberOfLogicalProcessors"] != null) logicalProcs += Convert.ToInt32(mo["NumberOfLogicalProcessors"]);
                }
                if (loads.Count > 0) health.CpuLoadPercent = loads.Average();
                health.LogicalProcessors = logicalProcs > 0 ? logicalProcs : 1;
            }

            using (var memSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem")))
            {
                foreach (ManagementObject mo in memSearcher.Get())
                {
                    var free = Convert.ToDouble(mo["FreePhysicalMemory"]);
                    var total = Convert.ToDouble(mo["TotalVisibleMemorySize"]);
                    if (total > 0) health.FreeMemoryPercent = free / total * 100.0;
                }
            }

            try
            {
                using var pqSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT ProcessorQueueLength FROM Win32_PerfFormattedData_PerfOS_System"));
                foreach (ManagementObject mo in pqSearcher.Get())
                {
                    if (mo["ProcessorQueueLength"] != null)
                        health.ProcessorQueueLength = Convert.ToInt64(mo["ProcessorQueueLength"]);
                }
            }
            catch { /* perf counter class not always present */ }

            if (health.CpuLoadPercent >= 90)
                health.Warnings.Add($"CPU load is {health.CpuLoadPercent:F0}% at capture time");

            if (health.FreeMemoryPercent >= 0 && health.FreeMemoryPercent < 5)
                health.Warnings.Add($"Free memory is only {health.FreeMemoryPercent:F1}%");

            if (health.ProcessorQueueLength >= 0 && health.ProcessorQueueLength > health.LogicalProcessors * 2)
                health.Warnings.Add($"Processor queue length ({health.ProcessorQueueLength}) is high relative to {health.LogicalProcessors} logical CPU(s) — possible thread starvation");

            var criticalServices = new[] { "RpcSs", "EventLog", "LanmanServer", "Dnscache" };
            foreach (var svcName in criticalServices)
            {
                var svc = services.FirstOrDefault(s => s.Name.Equals(svcName, StringComparison.OrdinalIgnoreCase));
                if (svc != null && !svc.State.Equals("Running", StringComparison.OrdinalIgnoreCase))
                    health.Warnings.Add($"Critical service '{svc.DisplayName}' is {svc.State}, not Running");
            }

            health.Status = health.Warnings.Count > 0 ? "Elevated / Needs Review" : "Healthy (WMI responsive, no red flags)";
        }
        catch (Exception ex)
        {
            health.Status = "Unknown";
            health.Warnings.Add($"Health check failed: {ex.Message}");
        }

        return health;
    }
}
