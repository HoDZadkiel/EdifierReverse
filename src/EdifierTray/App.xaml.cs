using System.Windows;
using EdifierTray.Service;
using EdifierTray.Ui;

namespace EdifierTray;

public partial class App : Application
{
    Mutex? _single;
    TrayHost? _host;

    static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EdifierTray", "crash.log");

    /// <summary>未處理的例外寫進 %APPDATA%\EdifierTray\crash.log（托盤程式沒有主控台，閃退時才查得到原因）。</summary>
    static void LogCrash(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) => { LogCrash("UI 執行緒", a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => LogCrash("AppDomain", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { LogCrash("背景工作", a.Exception); a.SetObserved(); };
        if (Cli.IsCliMode(e.Args))
        {
            int code = await Cli.RunAsync(e.Args);
            Shutdown(code);
            return;
        }

        _single = new Mutex(true, @"Local\EdifierTray.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }  // 已有一個在跑

        // 設定：第一次執行時依系統語言決定介面語言；外觀預設跟隨系統
        var directory = DeviceDirectory.Load();
        var settings = directory.Settings;
        if (settings.Language == "") { settings.Language = Strings.DetectSystemLanguage(); settings.Save(); }
        Strings.SetLanguage(settings.Language);
        Theme.Mode = settings.Theme;
        Strings.ApplyToResources(Resources);
        Theme.Apply(Theme.IsDark());
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() => Theme.Refresh());
        _host = new TrayHost(directory.MakeConnector(), directory: directory);
        _host.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _single?.ReleaseMutex();
        base.OnExit(e);
    }
}
