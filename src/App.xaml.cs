using System.IO;
using System.Threading;
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
        // одна программа за раз: второй запуск (двойной щелчок по файлу) передаёт файлы первой и закрывается
        var root = Environment.GetEnvironmentVariable("VERTUSHKA_TEST_ROOT") ?? "";
        pipeName = "Vertushka-" + (root.Length == 0 ? "main" : Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(root)))[..8]);
        bool first;
        try { single = new Mutex(true, pipeName, out first); } catch { first = true; }
        if (!first)
        {
            try
            {
                using var c = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.Out);
                c.Connect(1500);
                using var w = new StreamWriter(c, new System.Text.UTF8Encoding(false));
                w.WriteLine("show");
                foreach (var a in e.Args) w.WriteLine(System.IO.Path.GetFullPath(a));
                w.Flush();
                Shutdown();
                return;
            }
            catch { /* первая программа не отвечает — работаем как обычно */ }
        }
        else StartPipe();
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Slider), UIElement.PreviewMouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler(SliderDown));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Slider), UIElement.PreviewMouseMoveEvent, new System.Windows.Input.MouseEventHandler(SliderMove));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Slider), UIElement.PreviewMouseLeftButtonUpEvent, new System.Windows.Input.MouseButtonEventHandler(SliderUp));
        base.OnStartup(e);
    }

    static Mutex single;
    static string pipeName;

    void StartPipe()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var s = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.In, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                    await s.WaitForConnectionAsync();
                    using var r = new StreamReader(s, System.Text.Encoding.UTF8);
                    var lines = new List<string>();
                    string l;
                    while ((l = await r.ReadLineAsync()) != null) lines.Add(l);
                    for (int i = 0; i < 40; i++)
                    {
                        bool ok = false;
                        Dispatcher.Invoke(() => { if (MainWindow is MainWindow mw && mw.IsLoaded) { mw.OnExternalOpen(lines); ok = true; } });
                        if (ok) break;
                        await Task.Delay(250);
                    }
                }
                catch (Exception ex) { LogCrash("Pipe", ex); await Task.Delay(1000); }
            }
        });
    }

    // ползунок: нажал где угодно на полосе — значение сразу туда, и можно вести, не попадая в кружок
    static System.Windows.Controls.Slider dragSlider;

    static void SetFromMouse(System.Windows.Controls.Slider sl, System.Windows.Input.MouseEventArgs e)
    {
        if (sl.Template?.FindName("PART_Track", sl) is not System.Windows.Controls.Primitives.Track tr || tr.ActualWidth <= 0) return;
        double v = tr.ValueFromPoint(e.GetPosition(tr));
        sl.Value = Math.Clamp(v, sl.Minimum, sl.Maximum);
    }

    static void SliderDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Slider sl || !sl.IsEnabled) return;
        dragSlider = sl;
        sl.CaptureMouse();
        SetFromMouse(sl, e);
        e.Handled = true;
    }

    static void SliderMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not System.Windows.Controls.Slider sl || dragSlider != sl) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { dragSlider = null; sl.ReleaseMouseCapture(); return; }
        SetFromMouse(sl, e);
    }

    static void SliderUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Slider sl || dragSlider != sl) return;
        dragSlider = null;
        sl.ReleaseMouseCapture();
        e.Handled = true;
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
