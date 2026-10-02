using GoogleTasksDesktopWidget.Core;

namespace GoogleTasksDesktopWidget.Infrastructure;

public sealed class AppSettings
{
    public const int DefaultRefreshIntervalSeconds = 60;
    public const int MinRefreshIntervalSeconds = 10;
    public const int MaxRefreshIntervalSeconds = 86400;
    public int SchemaVersion { get; set; } = 4;
    public WidgetTheme Theme { get; set; } = WidgetTheme.Light;
    public bool IsLocked { get; set; }
    public bool StartWithWindows { get; set; }
    public string? MonitorDeviceName { get; set; }
    public double RightOffsetDip { get; set; } = 32;
    public double TopOffsetDip { get; set; } = 48;
    public double WidthDip { get; set; } = 340;
    public double HeightDip { get; set; } = 620;
    public double MaxHeightDip { get; set; } = 520;
    public string? SelectedListId { get; set; }
    public string? OAuthClientId { get; set; }
    public bool OAuthReauthorizationRequired { get; set; }
    public TaskFilter Filter { get; set; } = TaskFilter.All;
    public bool IsCompletedSectionExpanded { get; set; } = false;
    public TaskSortOrder SortOrder { get; set; } = TaskSortOrder.DueDate;
    public int RefreshIntervalSeconds { get; set; } = DefaultRefreshIntervalSeconds;
}

public enum WidgetTheme
{
    System,
    Light,
    Dark
}
