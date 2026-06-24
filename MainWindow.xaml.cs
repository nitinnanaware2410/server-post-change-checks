using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using ServerPostChangeChecks.Collectors;
using ServerPostChangeChecks.Diffing;
using ServerPostChangeChecks.Models;
using ServerPostChangeChecks.Persistence;
using ServerPostChangeChecks.Reporting;

namespace ServerPostChangeChecks;

public partial class MainWindow : Window
{
    private string _snapshotFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ServerPostChangeChecks");
    private string? _lastReportPath;
    private DateTime _lastBeforeUtc;
    private DateTime _lastAfterUtc;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(_snapshotFolder);
        SnapshotFolderTextBox.Text = _snapshotFolder;

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version == null ? "" : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    private List<string> GetServerList()
    {
        return ServersTextBox.Text
            .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private SecureString? GetSecurePassword()
    {
        if (string.IsNullOrEmpty(PasswordBox.Password)) return null;
        var sec = new SecureString();
        foreach (var c in PasswordBox.Password) sec.AppendChar(c);
        sec.MakeReadOnly();
        return sec;
    }

    private void Log(string message)
    {
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        LogTextBox.ScrollToEnd();
    }

    private void ImportServers_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Text files (*.txt;*.csv)|*.txt;*.csv|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true)
        {
            var content = File.ReadAllText(dlg.FileName);
            ServersTextBox.Text = content;
            Log($"Imported server list from {dlg.FileName}");
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = _snapshotFolder };
        if (dlg.ShowDialog() == true)
        {
            _snapshotFolder = dlg.FolderName;
            SnapshotFolderTextBox.Text = _snapshotFolder;
        }
    }

    private async void CaptureBefore_Click(object sender, RoutedEventArgs e) => await RunCaptureAsync("Before");

    private async void CaptureAfter_Click(object sender, RoutedEventArgs e) => await RunCaptureAsync("After");

    private async Task RunCaptureAsync(string label)
    {
        var servers = GetServerList();
        if (servers.Count == 0)
        {
            MessageBox.Show("Add at least one server.", "Server Post Change Checks", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, $"Capturing {label} snapshot across {servers.Count} server(s)...");
        string? user = string.IsNullOrWhiteSpace(UsernameTextBox.Text) ? null : UsernameTextBox.Text.Trim();
        var password = GetSecurePassword();

        int done = 0;
        var capturedAt = DateTime.UtcNow;

        var tasks = servers.Select(server => Task.Run(() =>
        {
            var snap = CimCollector.Collect(server, label, user, password);
            SnapshotStore.Save(snap, SnapshotStore.BuildFileName(_snapshotFolder, server, label));
            return snap;
        })).ToList();

        foreach (var t in tasks)
        {
            var snap = await t;
            done++;
            Dispatcher.Invoke(() =>
            {
                ProgressBarCtl.Value = (double)done / servers.Count * 100;
                Log(snap.Success
                    ? $"[{label}] {snap.ServerName}: captured OK (services={snap.Services.Count}, processes={snap.Processes.Count}, hotfixes={snap.Hotfixes.Count})"
                    : $"[{label}] {snap.ServerName}: FAILED - {snap.ErrorMessage}");
            });
        }

        if (label == "Before") _lastBeforeUtc = capturedAt; else _lastAfterUtc = capturedAt;

        SetBusy(false, $"{label} snapshot complete for {servers.Count} server(s).");
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        var servers = GetServerList();
        if (servers.Count == 0)
        {
            MessageBox.Show("Add at least one server.", "Server Post Change Checks", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, "Comparing snapshots...");

        var reports = new List<ServerDiffReport>();
        var summaryRows = new ObservableCollection<SummaryRow>();

        await Task.Run(() =>
        {
            foreach (var server in servers)
            {
                var beforePath = SnapshotStore.BuildFileName(_snapshotFolder, server, "Before");
                var afterPath = SnapshotStore.BuildFileName(_snapshotFolder, server, "After");
                var before = SnapshotStore.Load(beforePath);
                var after = SnapshotStore.Load(afterPath);

                if (before == null || after == null)
                {
                    Dispatcher.Invoke(() => Log($"{server}: missing Before and/or After snapshot — skipped. Run captures first."));
                    continue;
                }

                var report = SnapshotDiffer.Compare(before, after);
                reports.Add(report);

                summaryRows.Add(new SummaryRow
                {
                    ServerName = server,
                    BeforeStatus = before.Success ? "OK" : "Failed",
                    AfterStatus = after.Success ? "OK" : "Failed",
                    TotalChanges = report.TotalChanges,
                    ServiceChanges = report.Services.Items.Count,
                    ProcessChanges = report.Processes.Items.Count,
                    UserChanges = report.LoggedOnUsers.Items.Count,
                    HotfixChanges = report.Hotfixes.Items.Count,
                    TaskChanges = report.ScheduledTasks.Items.Count,
                    ErrorEvents = report.ErrorEvents.Items.Count,
                    AfterUptime = after.Success ? FormatUptime(after.Os.Uptime) : "",
                    HealthStatus = after.Success ? after.Health.Status : "",
                    OverallLabel = !before.Success || !after.Success ? "Capture error"
                        : report.TotalChanges == 0 ? "No changes"
                        : "Differences found"
                });
            }
        });

        SummaryGrid.ItemsSource = summaryRows;

        if (reports.Count > 0)
        {
            var html = HtmlReportBuilder.Build(reports, _lastBeforeUtc == default ? reports.Min(r => DateTime.UtcNow) : _lastBeforeUtc, _lastAfterUtc == default ? DateTime.UtcNow : _lastAfterUtc);
            var reportPath = Path.Combine(_snapshotFolder, $"Report_{DateTime.Now:yyyyMMdd_HHmmss}.html");
            File.WriteAllText(reportPath, html);
            _lastReportPath = reportPath;
            Log($"Report generated: {reportPath}");
        }
        else
        {
            Log("No reports generated — ensure both Before and After snapshots exist for at least one server.");
        }

        SetBusy(false, "Comparison complete.");
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReportPath == null || !File.Exists(_lastReportPath))
        {
            MessageBox.Show("No report has been generated yet.", "Server Post Change Checks", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(_lastReportPath) { UseShellExecute = true });
    }

    private static string FormatUptime(TimeSpan ts) => $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";

    private void SetBusy(bool busy, string status)
    {
        CaptureBeforeButton.IsEnabled = !busy;
        CaptureAfterButton.IsEnabled = !busy;
        CompareButton.IsEnabled = !busy;
        StatusText.Text = status;
        if (!busy) ProgressBarCtl.Value = 0;
        Log(status);
    }
}

public class SummaryRow
{
    public string ServerName { get; set; } = "";
    public string BeforeStatus { get; set; } = "";
    public string AfterStatus { get; set; } = "";
    public int TotalChanges { get; set; }
    public int ServiceChanges { get; set; }
    public int ProcessChanges { get; set; }
    public int UserChanges { get; set; }
    public int HotfixChanges { get; set; }
    public int TaskChanges { get; set; }
    public int ErrorEvents { get; set; }
    public string AfterUptime { get; set; } = "";
    public string HealthStatus { get; set; } = "";
    public string OverallLabel { get; set; } = "";
}
