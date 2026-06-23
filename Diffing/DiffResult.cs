using System.Collections.Generic;
using System.Linq;

namespace PatchHealthCheck.Diffing;

public enum DiffKind { Added, Removed, Changed, Warning }

public class DiffItem
{
    public DiffKind Kind { get; set; }
    public string Key { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool IsInformational { get; set; }
}

public class CategoryDiff
{
    public string CategoryName { get; set; } = "";
    public List<DiffItem> Items { get; set; } = new();
    public bool HasChanges => Items.Any(i => !i.IsInformational);
}

public class ServerDiffReport
{
    public string ServerName { get; set; } = "";
    public bool BeforeSuccess { get; set; }
    public bool AfterSuccess { get; set; }
    public string BeforeError { get; set; } = "";
    public string AfterError { get; set; } = "";

    public CategoryDiff Services { get; set; } = new() { CategoryName = "Services" };
    public CategoryDiff Processes { get; set; } = new() { CategoryName = "Processes / Apps" };
    public CategoryDiff LoggedOnUsers { get; set; } = new() { CategoryName = "Logged-on Users" };
    public CategoryDiff Hotfixes { get; set; } = new() { CategoryName = "Installed Hotfixes" };
    public CategoryDiff ScheduledTasks { get; set; } = new() { CategoryName = "Scheduled Tasks" };
    public CategoryDiff ErrorEvents { get; set; } = new() { CategoryName = "New Error Events" };
    public CategoryDiff Os { get; set; } = new() { CategoryName = "OS / Reboot" };

    private static int RealCount(CategoryDiff c) => c.Items.Count(i => !i.IsInformational);

    public int TotalChanges =>
        RealCount(Services) + RealCount(Processes) + RealCount(LoggedOnUsers) +
        RealCount(Hotfixes) + RealCount(ScheduledTasks) + RealCount(ErrorEvents) + RealCount(Os);
}
