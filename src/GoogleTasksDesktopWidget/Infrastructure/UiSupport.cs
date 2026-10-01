using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace GoogleTasksDesktopWidget.Infrastructure;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    private bool _isRunning;
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && (canExecute?.Invoke(parameter) ?? true);
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter)) return;
        _isRunning = true;
        NotifyCanExecuteChanged();
        try
        {
            await execute(parameter);
        }
        catch (Exception exception)
        {
            // Commands surface expected failures through view-model status text.
            AppLogger.WriteError("AsyncCommand", exception);
        }
        finally
        {
            _isRunning = false;
            NotifyCanExecuteChanged();
        }
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class DepthIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        var depth = value is int number ? Math.Clamp(number, 0, 8) : 0;
        return new Thickness(4 + depth * 14, 2, 0, 2);
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        Binding.DoNothing;
}

public static class UiText
{
    public static string Get(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}

public static class ThemeManager
{
    public static bool Apply(WidgetTheme mode)
    {
        var dark = mode switch
        {
            WidgetTheme.Dark => true,
            WidgetTheme.Light => false,
            _ => ReadSystemDarkPreference()
        };
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var themeUri = new Uri(dark ? "/GoogleTasksDesktopWidget;component/Themes/Dark.xaml" : "/GoogleTasksDesktopWidget;component/Themes/Light.xaml", UriKind.Relative);
        var theme = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains("Themes/", StringComparison.OrdinalIgnoreCase) == true);
        if (theme is not null)
        {
            var index = dictionaries.IndexOf(theme);
            dictionaries[index] = new ResourceDictionary { Source = themeUri };
        }
        return dark;
    }

    private static bool ReadSystemDarkPreference()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("AppsUseLightTheme") is int lightTheme && lightTheme == 0;
        }
        catch
        {
            return false;
        }
    }
}
