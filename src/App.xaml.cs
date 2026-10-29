using System.IO;
using System.Windows;

namespace Vertushka;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // необработанные ошибки — в crash.log в папке данных, чтобы вылет можно было разобрать
        DispatcherUnhandledException += (s, a) => { LogCrash("UI", a.Exception); a.Handled = true; }; // на мероприятии лучше пропустить сбойный такт, чем потерять музыку
        AppDomain.CurrentDomain.UnhandledException += (s, a) => LogCrash("Domain", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (s, a) => LogCrash("Task", a.Exception);
        Theme.Apply(Settings.Load().Theme);
        base.OnStartup(e);
    }

    public static void LogCrash(string kind, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Paths.AppData);
            File.AppendAllText(System.IO.Path.Combine(Paths.AppData, "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{kind}] {ex}\r\n\r\n");
        }
        catch { }
    }
}
