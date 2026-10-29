using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;

namespace Vertushka;

// Обрезка трека в новый файл: mp3 режем по кадрам без потери качества, остальное сохраняем как WAV.
public static class Trimmer
{
    public static string UniquePath(string dir, string name)
    {
        var dest = System.IO.Path.Combine(dir, name);
        var stem = System.IO.Path.GetFileNameWithoutExtension(name); var ext = System.IO.Path.GetExtension(name);
        for (int i = 2; File.Exists(dest); i++) dest = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
        return dest;
    }

    public static string OutExt(string src) => System.IO.Path.GetExtension(src).ToLowerInvariant() == ".mp3" ? ".mp3" : ".wav";

    public static TimeSpan Length(string path)
    {
        using var r = new AudioFileReader(path);
        return r.TotalTime;
    }

    public static void Trim(string src, double startSec, double endSec, string dest)
    {
        if (endSec <= startSec) throw new ArgumentException("Конец должен быть позже начала");
        try
        {
            if (OutExt(src) == ".mp3") TrimMp3(src, startSec, endSec, dest);
            else TrimWav(src, startSec, endSec, dest);
        }
        catch { try { File.Delete(dest); } catch { } throw; } // пустой огрызок в разделе не оставляем
    }

    static void TrimMp3(string src, double start, double end, string dest)
    {
        try { TrimMp3Core(src, start, end, dest); }
        catch { try { File.Delete(dest); } catch { } throw; } // пустой огрызок в разделе не оставляем
    }

    static void TrimMp3Core(string src, double start, double end, string dest)
    {
        using var r = new Mp3FileReader(src);
        using var fs = File.Create(dest);
        double t = 0; int written = 0;
        Mp3Frame f;
        while ((f = r.ReadNextFrame()) != null)
        {
            double d = f.SampleCount / (double)f.SampleRate;
            if (t >= end) break;
            if (t + d > start) { fs.Write(f.RawData, 0, f.RawData.Length); written++; }
            t += d;
        }
        if (written == 0) throw new InvalidOperationException("В этом куске нет звука");
    }

    static void TrimWav(string src, double start, double end, string dest)
    {
        using var r = new AudioFileReader(src);
        r.CurrentTime = TimeSpan.FromSeconds(start);
        var p16 = new NAudio.Wave.SampleProviders.SampleToWaveProvider16(r);
        long bytes = (long)((end - start) * p16.WaveFormat.AverageBytesPerSecond);
        bytes -= bytes % p16.WaveFormat.BlockAlign;
        using var w = new WaveFileWriter(dest, p16.WaveFormat);
        var buf = new byte[p16.WaveFormat.AverageBytesPerSecond / 2 - (p16.WaveFormat.AverageBytesPerSecond / 2) % p16.WaveFormat.BlockAlign];
        long left = bytes;
        while (left > 0)
        {
            int n = p16.Read(buf, 0, (int)Math.Min(buf.Length, left));
            if (n <= 0) break;
            w.Write(buf, 0, n);
            left -= n;
        }
    }

    public static string Fmt(double sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }

    // «1:23», «1:23,4», «83» → секунды; не разобрали — null
    public static double? Parse(string s)
    {
        s = (s ?? "").Trim().Replace(',', '.');
        if (s == "") return null;
        var parts = s.Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 0 || !double.IsFinite(v)) return null;
            total = total * 60 + v;
        }
        return total;
    }
}

public static class TrimDialog
{
    // возвращает путь нового файла или null
    public static string Show(Window owner, Song song, int device = -1)
    {
        Brush Res(string k) => (Brush)Application.Current.Resources[k];
        double total;
        try { total = Trimmer.Length(song.Path).TotalSeconds; }
        catch { MessageBox.Show(owner, "Не получилось открыть файл", "Обрезать трек"); return null; }

        var cues = Cues.Get(song.Path).Where(c => c >= 0).OrderBy(c => c).ToList();
        var w = new Window
        {
            Title = "Обрезать трек", Owner = owner, Width = 560, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = owner.Topmost, Background = Res("MenuBg")
        };
        var root = new StackPanel { Margin = new Thickness(20) };
        TextBlock T(string s, double size = 15, string brush = "Text", double bottom = 8) =>
            new() { Text = s, FontSize = size, Foreground = Res(brush), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, bottom) };

        root.Children.Add(T(song.Name, 18, "Text", 2));
        root.Children.Add(T($"Длина трека {Trimmer.Fmt(total)}. Исходный файл не меняется — рядом появится новый.", 13, "Muted", 14));

        var startS = new Slider { Minimum = 0, Maximum = total, Value = 0, Margin = new Thickness(0, 0, 0, 6) };
        var endS = new Slider { Minimum = 0, Maximum = total, Value = total, Margin = new Thickness(0, 0, 0, 6) };
        var startT = new TextBox { Width = 90, Padding = new Thickness(6), FontSize = 15, Text = Trimmer.Fmt(0) };
        var endT = new TextBox { Width = 90, Padding = new Thickness(6), FontSize = 15, Text = Trimmer.Fmt(total) };
        var info = T("", 15, "Accent", 10);
        var msg = T("", 14, "Muted", 8);

        UIElement Line(string label, Slider s, TextBox tb, Func<double?> cue, string cueLabel)
        {
            var g = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            head.Children.Add(new TextBlock { Text = label, FontSize = 15, Foreground = Res("Text"), VerticalAlignment = VerticalAlignment.Center, Width = 70 });
            head.Children.Add(tb);
            var here = new Button { Content = cueLabel, Style = (Style)Application.Current.Resources["Flat"], Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(10, 0, 0, 0), IsEnabled = cue() != null, ToolTip = cue() == null ? "В этом треке нет меток" : cueLabel };
            here.Click += (a, b) => { if (cue() is double c) s.Value = c; };
            head.Children.Add(here);
            g.Children.Add(head); g.Children.Add(s);
            return g;
        }
        root.Children.Add(Line("Начало", startS, startT, () => cues.Count > 0 ? cues[0] : null, "Первая метка"));
        root.Children.Add(Line("Конец", endS, endT, () => cues.Count > 1 ? cues[^1] : (cues.Count == 1 ? cues[0] : null), "Последняя метка"));
        root.Children.Add(info);
        root.Children.Add(msg);

        bool sync = false;
        void Refresh()
        {
            info.Text = $"Останется кусок {Trimmer.Fmt(Math.Max(0, endS.Value - startS.Value))} · сохранится как {Trimmer.OutExt(song.Path).TrimStart('.').ToUpperInvariant()}";
        }
        startS.ValueChanged += (a, b) => { if (sync) return; sync = true; if (startS.Value > endS.Value) endS.Value = startS.Value; startT.Text = Trimmer.Fmt(startS.Value); sync = false; Refresh(); };
        endS.ValueChanged += (a, b) => { if (sync) return; sync = true; if (endS.Value < startS.Value) startS.Value = endS.Value; endT.Text = Trimmer.Fmt(endS.Value); sync = false; Refresh(); };
        void Commit(TextBox tb, Slider s) { if (Trimmer.Parse(tb.Text) is double v) s.Value = Math.Min(total, v); else tb.Text = Trimmer.Fmt(s.Value); }
        // Enter в поле времени применяет время (а не сохраняет файл)
        startT.KeyDown += (a, b) => { if (b.Key == System.Windows.Input.Key.Enter) { Commit(startT, startS); b.Handled = true; } };
        endT.KeyDown += (a, b) => { if (b.Key == System.Windows.Input.Key.Enter) { Commit(endT, endS); b.Handled = true; } };
        startT.LostFocus += (a, b) => Commit(startT, startS);
        endT.LostFocus += (a, b) => Commit(endT, endS);
        Refresh();

        // прослушать кусок на основном выходе
        WaveOutEvent po = null; AudioFileReader pr = null;
        var pt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var listen = new Button { Content = "Послушать кусок", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.Resources["Flat"] };
        void StopListen()
        {
            pt.Stop();
            try { po?.Stop(); } catch { }
            po?.Dispose(); pr?.Dispose(); po = null; pr = null;
            listen.Content = "Послушать кусок";
        }
        pt.Tick += (a, b) => { if (pr == null || pr.CurrentTime.TotalSeconds >= endS.Value) StopListen(); };
        listen.Click += (a, b) =>
        {
            if (po != null) { StopListen(); return; }
            try
            {
                pr = new AudioFileReader(song.Path) { CurrentTime = TimeSpan.FromSeconds(startS.Value) };
                po = new WaveOutEvent { DeviceNumber = device >= 0 ? device : -1 };
                po.Init(pr); po.Play(); pt.Start();
                listen.Content = "Стоп";
                msg.Foreground = Res("Muted");
                msg.Text = device >= 0 ? "Играет в наушниках" : "Играет на основном выходе (наушники не выбраны в меню ⚙)";
            }
            catch { StopListen(); msg.Text = "Не получилось проиграть кусок"; }
        };

        string result = null;
        var save = new Button { Content = "Сохранить как новый файл", IsDefault = false, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.Resources["AccentBtn"] };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(16, 8, 16, 8), Style = (Style)Application.Current.Resources["Flat"] };
        save.Click += async (a, b) =>
        {
            StopListen();
            if (endS.Value - startS.Value < 0.5) { msg.Foreground = Res("RecText"); msg.Text = "Кусок слишком короткий"; return; }
            try
            {
                var dir = System.IO.Path.GetDirectoryName(song.Path);
                var name = Path_Safe(System.IO.Path.GetFileNameWithoutExtension(song.Path)) + " (обрезка)" + Trimmer.OutExt(song.Path);
                var dest = Trimmer.UniquePath(dir, name);
                save.IsEnabled = false; msg.Foreground = Res("Muted"); msg.Text = "Сохраняю…";
                double s0 = startS.Value, e0 = endS.Value;
                cancel.IsEnabled = false;
                await Task.Run(() => Trimmer.Trim(song.Path, s0, e0, dest)); // длинный файл не вешает окно
                result = dest;
                w.DialogResult = true;
            }
            catch (Exception ex) { save.IsEnabled = true; cancel.IsEnabled = true; msg.Foreground = Res("RecText"); msg.Text = "Не получилось: " + ex.Message; }
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(listen); row.Children.Add(save); row.Children.Add(cancel);
        root.Children.Add(row);
        w.Content = root;
        w.Closed += (a, b) => StopListen();
        w.ShowDialog();
        return result;
    }

    static string Path_Safe(string s) => string.Concat(s.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
