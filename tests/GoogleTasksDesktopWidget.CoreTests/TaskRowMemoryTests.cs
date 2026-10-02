using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Input;
using System.Reflection;
using GoogleTasksDesktopWidget;
using GoogleTasksDesktopWidget.Core.Models;
using GoogleTasksDesktopWidget.Infrastructure;
using GoogleTasksDesktopWidget.ViewModels;
using GoogleTasksDesktopWidget.Windows;

internal static class TaskRowMemoryTests
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckRows(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("Task row memory test failed", failure);
    }

    private static void CheckRows()
    {
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/GoogleTasksDesktopWidget;component/Themes/Light.xaml", UriKind.Relative)
        });
        var settings = new AppSettings();
        var settingsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var store = new SettingsStore(settingsPath);
        var credentials = new CredentialStore();
        var oauth = new OAuthService(credentials, settings, store);
        using var host = new DesktopHostService(settings, store);
        var model = new MainViewModel(settings, store, credentials, oauth,
            new GoogleTasksClient(oauth), new EncryptedCacheStore(), new AutoStartManager());
        var window = new MainWindow(model, host);
        var template = (DataTemplate)window.Resources["TaskRow"];
        var task = new TaskItemViewModel(new OrderedTask(new TaskRecord(
            "row", "Title", "needsAction", null, null, "1", "list", "Notes"), 0));
        var row = (FrameworkElement)template.LoadContent();
        row.DataContext = task;
        Layout(row);
        var closedEditors = Descendants(row).OfType<TextBox>().Count();
        if (closedEditors != 0) throw new Exception("Closed rows eagerly created text editors.");
        Console.WriteLine($"ROW closed TextBoxes: {closedEditors}");

        task.BeginDetailsEditing();
        Layout(row);
        var title = Descendants(row).OfType<TextBox>().Single(box => box.Name == "InlineTaskTitle");
        var notes = Descendants(row).OfType<TextBox>().Single(box => box.Name == "InlineTaskNotes");
        title.Text = "Edited title";
        notes.Text = "Edited notes";
        if (task.EditDraft != title.Text || task.DraftNotes != notes.Text)
            throw new Exception("Editor bindings did not update drafts.");
        var button = Descendants(row).OfType<Button>().Single(item => item.Name == "InlineTaskCalendarButton");
        var popup = Descendants(row).OfType<System.Windows.Controls.Primitives.Popup>().Single();
        if (!ReferenceEquals(popup.PlacementTarget, button) || popup.Child is null)
            throw new Exception("Calendar popup lost its placement target or content.");
        task.IsDetailsExpanded = false;
        Layout(row);
        if (Descendants(row).OfType<TextBox>().Any())
            throw new Exception("Closing a row retained its text editors.");
        task.BeginDetailsEditing();
        Layout(row);
        if (Descendants(row).OfType<TextBox>().Count() != 2)
            throw new Exception("Editors could not be reopened.");

        CheckOutsideClicks(window, row, task);

        // Warm the templates first, then measure managed bytes retained by 200 closed rows.
        task.IsDetailsExpanded = false;
        Layout(row);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalMemory(true);
        var rows = new List<FrameworkElement>();
        for (var index = 0; index < 200; index++)
        {
            var item = (FrameworkElement)template.LoadContent();
            item.DataContext = task;
            Layout(item);
            if (!ReferenceEquals(((Border)item).ContextMenu, ((Border)row).ContextMenu))
                throw new Exception("Rows allocated separate context menus.");
            rows.Add(item);
        }
        var menu = ((Border)rows[0]).ContextMenu;
        menu.PlacementTarget = rows[0];
        Layout(rows[0]);
        if (!ReferenceEquals(menu.DataContext, task))
            throw new Exception("Shared menu did not bind to its current task.");
        var otherTask = new TaskItemViewModel(new OrderedTask(task.Task with { Id = "other" }, 0));
        rows[1].DataContext = otherTask;
        menu.PlacementTarget = rows[1];
        Layout(rows[1]);
        if (!ReferenceEquals(menu.DataContext, otherTask))
            throw new Exception("Shared menu retained the previous task.");
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent, menu));
        Layout(rows[1]);
        if (menu.PlacementTarget is not null || menu.DataContext is not null)
            throw new Exception("Closed shared menu retained its task row.");
        var retained = GC.GetTotalMemory(true) - before;
        Console.WriteLine($"ROW 200 closed rows retained managed bytes: {retained:N0}");
        GC.KeepAlive(rows);
        Console.WriteLine("PASS Task row editors bind, close, and reopen with calendar placement intact");
        CheckRefreshIntervals(window, model, store, settingsPath);
    }

    private static void Layout(FrameworkElement row)
    {
        row.Measure(new Size(340, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, 340, row.DesiredSize.Height));
        row.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void CheckOutsideClicks(MainWindow window, FrameworkElement row, TaskItemViewModel task)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Open(TaskItemViewModel item) => typeof(MainWindow).GetMethod("OpenTaskDetails", flags)!
            .Invoke(window, [row, item, false]);
        void Click(DependencyObject source)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
                Source = source
            };
            typeof(MainWindow).GetMethod("OnWidgetSurfacePreviewMouseLeftButtonUp", flags)!
                .Invoke(window, [window, PrepareClick(args)]);
            Layout(row);
        }
        MouseButtonEventArgs PrepareClick(MouseButtonEventArgs args)
        {
            typeof(MainWindow).GetMethod("OnWidgetPreviewMouseLeftButtonDown", flags)!
                .Invoke(window, [window, args]);
            return args;
        }

        task.CancelDetailsEditing();
        Open(task);
        Layout(row);
        Click(Descendants(row).OfType<TextBox>().First());
        if (!task.IsDetailsExpanded) throw new Exception("Clicking an editor closed the task.");
        Click(new Border());
        if (task.IsDetailsExpanded) throw new Exception("Clicking outside did not close an unchanged task.");

        Open(task);
        Layout(row);
        Click(row);
        if (task.IsDetailsExpanded) throw new Exception("Clicking row padding did not close the editor.");

        Open(task);
        Layout(row);
        Click(Descendants(row).OfType<Grid>().First());
        if (task.IsDetailsExpanded) throw new Exception("Clicking row whitespace did not close the editor.");

        // The mouse-up which follows opening an editor must not immediately close it.
        Open(task);
        typeof(MainWindow).GetMethod("OnWidgetSurfacePreviewMouseLeftButtonUp", flags)!
            .Invoke(window, [window, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
                Source = row
            }]);
        Layout(row);
        if (!task.IsDetailsExpanded) throw new Exception("The opening click closed the new editor.");

        Open(task);
        task.EditDraft = "Changed draft";
        Click(new Border());
        // No selected list is configured in this test, so saving must fail without contacting Google.
        if (!task.IsDetailsExpanded || task.EditDraft != "Changed draft")
            throw new Exception("A failed save discarded the open editor or draft.");
        task.CancelDetailsEditing();

        var draft = new TaskItemViewModel(new OrderedTask(task.Task with { Id = "draft", Title = "", Notes = null }, 0), isDraft: true);
        typeof(MainWindow).GetField("_draftTask", flags)!.SetValue(window, draft);
        row.DataContext = draft;
        Open(draft);
        Click(new Border());
        if (typeof(MainWindow).GetField("_draftTask", flags)!.GetValue(window) is not null)
            throw new Exception("Clicking outside retained a blank new task.");
        row.DataContext = task;
        Console.WriteLine("PASS Outside clicks close existing tasks and blank drafts, preserve inside clicks and failed saves");
    }

    private static void CheckRefreshIntervals(MainWindow window, MainViewModel model, SettingsStore store, string settingsPath)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var timer = (DispatcherTimer)typeof(MainViewModel).GetField("_pollTimer", flags)!.GetValue(model)!;
        try
        {
            if (model.RefreshIntervalSeconds != 60 || timer.Interval != TimeSpan.FromSeconds(60))
                throw new Exception("The default refresh interval changed.");
            if (!model.SetRefreshIntervalSeconds(125) || store.Load().RefreshIntervalSeconds != 125 ||
                timer.Interval != TimeSpan.FromSeconds(125))
                throw new Exception("Custom interval was not saved or applied to the timer.");
            if (model.SetRefreshIntervalSeconds(9) || model.SetRefreshIntervalSeconds(86401) || model.RefreshIntervalSeconds != 125)
                throw new Exception("Invalid intervals changed the saved interval.");

            var box = (TextBox)window.FindName("RefreshIntervalBox");
            var error = (TextBlock)window.FindName("RefreshIntervalError");
            box.Text = "abc";
            typeof(MainWindow).GetMethod("ApplyRefreshInterval", flags)!.Invoke(window, null);
            if (error.Visibility != Visibility.Visible || model.RefreshIntervalSeconds != 125)
                throw new Exception("Invalid custom input was not rejected.");
            box.Text = "300";
            typeof(MainWindow).GetMethod("ApplyRefreshInterval", flags)!.Invoke(window, null);
            if (store.Load().RefreshIntervalSeconds != 300 || timer.Interval != TimeSpan.FromSeconds(300))
                throw new Exception("Custom interval UI did not apply the setting.");

            File.WriteAllText(settingsPath, "{\"schemaVersion\":4}");
            if (store.Load().RefreshIntervalSeconds != 60) throw new Exception("Old settings did not default to 60 seconds.");
            File.WriteAllText(settingsPath, "{\"schemaVersion\":4,\"refreshIntervalSeconds\":0}");
            if (store.Load().RefreshIntervalSeconds != 60) throw new Exception("Invalid saved interval was not normalized.");
            File.Delete(settingsPath);
            Directory.CreateDirectory(settingsPath);
            if (model.SetRefreshIntervalSeconds(900) || model.RefreshIntervalSeconds != 300 || timer.Interval != TimeSpan.FromSeconds(300))
                throw new Exception("Failed interval persistence changed the setting or timer.");
            Console.WriteLine("PASS Refresh intervals persist, update the timer, validate custom input, and roll back failed saves");
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (Directory.Exists(settingsPath)) Directory.Delete(settingsPath);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
