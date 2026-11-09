using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace DiffDesk.Desktop;

public partial class App : Application
{
    private FileStream? _instanceLockStream;
    private FileSystemWatcher? _launchRequestWatcher;
    private string _launchRequestPath = string.Empty;
    private string _settingsPath = string.Empty;

    public static string GetDataDirectory()
    {
        var configuredDirectory = Environment.GetEnvironmentVariable("DIFFDESK_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configuredDirectory)) return Path.GetFullPath(configuredDirectory);

        var portableDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
        return Directory.Exists(portableDirectory)
            ? portableDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffDesk");
    }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var dataDirectory = GetDataDirectory();
        Directory.CreateDirectory(dataDirectory);
        try
        {
            _instanceLockStream = new FileStream(Path.Combine(dataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            SendArgumentsToRunningInstance(dataDirectory, e.Args);
            Shutdown();
            return;
        }

        _settingsPath = Path.Combine(dataDirectory, "settings.json");
        _launchRequestPath = Path.Combine(dataDirectory, "launch-request.json");
        try { File.Delete(_launchRequestPath); }
        catch (IOException) { }
        StartLaunchRequestWatcher(dataDirectory);
        ApplySavedTheme();
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            if (e.Args.Length > 0) window.OpenArguments(e.Args);
        }
        catch (Exception exception)
        {
            try { File.WriteAllText(Path.Combine(dataDirectory, "startup-error.txt"), exception.ToString()); }
            catch (IOException) { }
            MessageBox.Show(exception.ToString(), "DiffDesk نتوانست پنجره را باز کند", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public void ToggleTheme()
    {
        var next = IsLightTheme ? "dark" : "light";
        ApplyTheme(next);
        SaveTheme(next);
    }

    public bool IsLightTheme => (Resources["AppBackground"] as SolidColorBrush)?.Color == Color.FromRgb(0xF1, 0xF5, 0xFA);

    private void ApplySavedTheme()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            using var json = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            ApplyTheme(json.RootElement.TryGetProperty("theme", out var theme) ? theme.GetString() ?? "dark" : "dark");
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
    }

    private void SaveTheme(string theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new { theme }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void ApplyTheme(string theme)
    {
        var light = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
        var palette = light
            ? new Dictionary<string, string>
            {
                ["AppBackground"] = "#F1F5FA", ["SurfaceBrush"] = "#FFFFFF", ["SurfaceRaisedBrush"] = "#E8EEF5", ["SurfaceInputBrush"] = "#F7F9FC",
                ["BorderBrush"] = "#D5DFEA", ["TextBrush"] = "#142234", ["MutedTextBrush"] = "#52637A", ["SubtleTextBrush"] = "#71819A",
                ["AccentBrush"] = "#087F80", ["AccentPressedBrush"] = "#08696B", ["AccentSoftBrush"] = "#D8F0EE", ["GoodBrush"] = "#16835B",
                ["WarningBrush"] = "#A66400", ["DangerBrush"] = "#B83D4D", ["CodeBrush"] = "#F7F9FC", ["DiffLeftBrush"] = "#F9E4E8", ["DiffRightBrush"] = "#DDF2E8"
            }
            : new Dictionary<string, string>
            {
                ["AppBackground"] = "#0B1220", ["SurfaceBrush"] = "#111B2E", ["SurfaceRaisedBrush"] = "#17243A", ["SurfaceInputBrush"] = "#0D1728",
                ["BorderBrush"] = "#26344D", ["TextBrush"] = "#F2F6FC", ["MutedTextBrush"] = "#A4B2C7", ["SubtleTextBrush"] = "#71819A",
                ["AccentBrush"] = "#46D6C8", ["AccentPressedBrush"] = "#2CB7AA", ["AccentSoftBrush"] = "#173B42", ["GoodBrush"] = "#43D49D",
                ["WarningBrush"] = "#F6BE65", ["DangerBrush"] = "#FF7B82", ["CodeBrush"] = "#0A1220", ["DiffLeftBrush"] = "#3D222C", ["DiffRightBrush"] = "#17372F"
            };

        foreach (var (key, value) in palette)
            Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    }

    private void StartLaunchRequestWatcher(string dataDirectory)
    {
        _launchRequestWatcher = new FileSystemWatcher(dataDirectory, Path.GetFileName(_launchRequestPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false
        };
        _launchRequestWatcher.Created += LaunchRequestWatcher_Changed;
        _launchRequestWatcher.Changed += LaunchRequestWatcher_Changed;
        _launchRequestWatcher.Renamed += LaunchRequestWatcher_Renamed;
        _launchRequestWatcher.EnableRaisingEvents = true;
    }

    private void LaunchRequestWatcher_Changed(object sender, FileSystemEventArgs e) => QueueLaunchRequest();
    private void LaunchRequestWatcher_Renamed(object sender, RenamedEventArgs e) => QueueLaunchRequest();

    private void QueueLaunchRequest()
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            var processingPath = Path.Combine(Path.GetDirectoryName(_launchRequestPath)!, $"launch-request.{Guid.NewGuid():N}.processing");
            try
            {
                File.Move(_launchRequestPath, processingPath);
                var args = JsonSerializer.Deserialize<string[]>(File.ReadAllText(processingPath));
                if (args is not null) (MainWindow as MainWindow)?.OpenArguments(args);
            }
            catch (IOException) { }
            catch (JsonException) { }
            finally
            {
                try { File.Delete(processingPath); }
                catch (IOException) { }
            }
        }));
    }

    private static void SendArgumentsToRunningInstance(string dataDirectory, string[] args)
    {
        var requestPath = Path.Combine(dataDirectory, "launch-request.json");
        var temporaryPath = Path.Combine(dataDirectory, $"launch-request.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(args));
        File.Move(temporaryPath, requestPath, overwrite: true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _launchRequestWatcher?.Dispose();
        _instanceLockStream?.Dispose();
        base.OnExit(e);
    }
}
