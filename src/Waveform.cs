using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.Wave;

namespace Vertushka;

// Настоящая волна дорожки: считается по звуку файла, запоминается в кэше.
public static class WaveformCache
{
    public const int Bins = 1000;

    static string Dir => System.IO.Path.Combine(Paths.AppData, "waves");

    static string CachePath(string file)
    {
        var fi = new FileInfo(file);
        var key = $"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return System.IO.Path.Combine(Dir, hash + ".wf");
    }

    // убираем кэши, которых не открывали больше двух месяцев
    public static void Cleanup() => Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var old = DateTime.UtcNow.AddDays(-60);
            foreach (var f in Directory.EnumerateFiles(Dir, "*.wf"))
                if (File.GetLastAccessTimeUtc(f) < old && File.GetLastWriteTimeUtc(f) < old) File.Delete(f);
        }
        catch { }
    });

    public static Task<float[]> GetAsync(string file) => Task.Run(() =>
    {
        try
        {
            var cache = CachePath(file);
            if (File.Exists(cache))
            {
                var bytes = File.ReadAllBytes(cache);
                if (bytes.Length == Bins * 4)
                {
                    var arr = new float[Bins];
                    Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
                    return arr;
                }
            }
            var peaks = Compute(file);
            if (peaks != null)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var bytes = new byte[Bins * 4];
                    Buffer.BlockCopy(peaks, 0, bytes, 0, bytes.Length);
                    File.WriteAllBytes(cache, bytes);
                }
                catch { }
            }
            return peaks;
        }
        catch { return null; }
    });

    // пики для куска трека [start, start+len) с bins столбиками — для приближённой волны
    public static float[] Window(string file, double start, double len, int bins = 1000)
    {
        try
        {
            using var r = new AudioFileReader(file);
            int ch = r.WaveFormat.Channels, sr = r.WaveFormat.SampleRate;
            start = Math.Clamp(start, 0, r.TotalTime.TotalSeconds);
            r.CurrentTime = TimeSpan.FromSeconds(start);
            long frames = Math.Max(1, (long)(len * sr));
            var peaks = new float[bins];
            var buf = new float[4096 * ch];
            long fi = 0;
            while (fi < frames)
            {
                int want = (int)Math.Min(4096, frames - fi);
                int n = r.Read(buf, 0, want * ch);
                if (n <= 0) break;
                int got = n / ch;
                for (int f = 0; f < got; f++, fi++)
                {
                    int b = (int)(fi * bins / frames);
                    if (b >= bins) b = bins - 1;
                    float m = 0;
                    for (int c = 0; c < ch; c++) m = Math.Max(m, Math.Abs(buf[f * ch + c]));
                    if (m > peaks[b]) peaks[b] = m;
                }
            }
            float refv = Math.Max(peaks.Max(), 0.15f); // тихий кусок не раздуваем до полной высоты
            for (int i = 0; i < bins; i++) peaks[i] = (float)Math.Pow(Math.Min(1f, peaks[i] / refv), 0.8);
            return peaks;
        }
        catch { return null; }
    }

    static float[] Compute(string file)
    {
        using var r = new AudioFileReader(file);
        long total = r.Length / 4;
        if (total <= 0) return null;
        double perBin = Math.Max(1.0, (double)total / Bins);
        var peaks = new float[Bins];
        var buf = new float[16384];
        long idx = 0;
        int n;
        while ((n = r.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++, idx++)
            {
                int b = (int)(idx / perBin);
                if (b >= Bins) b = Bins - 1;
                float v = Math.Abs(buf[i]);
                if (v > peaks[b]) peaks[b] = v;
            }
        }
        float max = peaks.Max();
        if (max <= 0) return peaks;
        for (int i = 0; i < Bins; i++) peaks[i] = (float)Math.Pow(peaks[i] / max, 0.8);
        return peaks;
    }
}

public class WaveformControl : FrameworkElement
{
    float[] peaks;
    double progress;
    bool dragging;

    public bool Dragging => dragging;

    public event Action<double, bool> Scrub; // доля 0..1, true — отпустили кнопку

    // кисти берём из темы при каждой отрисовке (тема может смениться на ходу)
    static Brush Played => Theme.Get("Accent");
    static Brush Rest => Theme.Get("WaveRest");
    static Brush Needle => Theme.Get("Text");

    public WaveformControl()
    {
        Cursor = System.Windows.Input.Cursors.Hand;
        Action redraw = () => InvalidateVisual();
        Loaded += (_, _) => Theme.Changed += redraw;
        Unloaded += (_, _) => Theme.Changed -= redraw;
    }

    public float[] Peaks { get => peaks; set { peaks = value; shape = null; InvalidateVisual(); } }

    bool loading;
    // пока волна считается — на ней подпись
    public bool Loading { get => loading; set { if (loading == value) return; loading = value; InvalidateVisual(); } }

    double duration, fadeSec;
    bool axis;
    // длина трека в секундах (для шкалы времени и красной зоны затухания)
    public double Duration { get => duration; set { if (Math.Abs(duration - value) < 0.05) return; duration = value; InvalidateVisual(); } }
    // сколько секунд в конце трека затухание; 0 — зона не рисуется
    public double FadeSeconds { get => fadeSec; set { if (Math.Abs(fadeSec - value) < 0.05) return; fadeSec = value; InvalidateVisual(); } }
    // шкала времени, подписи уровней и пузырёк «прошло · осталось» (в узкой полосе выключено)
    public bool ShowAxis { get => axis; set { if (axis == value) return; axis = value; shape = null; InvalidateVisual(); } }

    // метки: секунды, -1 — нет; флажки с номерами рисуются, когда включена шкала
    double[] cues;
    public double[] Cues { get => cues; set { cues = value == null ? null : (double[])value.Clone(); InvalidateVisual(); } }
    public event Action<int> CueJump;    // клик по флажку
    public event Action<int> CueRemove;  // правый клик по флажку

    double CueX(int i) => Gutter + cues[i] / Math.Max(1, duration) * Math.Max(1, ActualWidth - Gutter);

    int CueAt(Point p)
    {
        if (!axis || cues == null || duration <= 1 || p.Y > 28) return -1;
        for (int i = 0; i < cues.Length; i++)
            if (cues[i] >= 0 && Math.Abs(p.X - CueX(i)) <= 14) return i;
        return -1;
    }

    // приближённая волна (режим обрезки): окно трека показывается «как весь трек», подписи времени сдвигаются на TimeOffset
    double timeOffset; bool showBubble = true;
    public double TimeOffset { get => timeOffset; set { if (timeOffset == value) return; timeOffset = value; InvalidateVisual(); } }
    public bool ShowBubble { get => showBubble; set { if (showBubble == value) return; showBubble = value; InvalidateVisual(); } }

    static string FmtT(double x, double step)
    {
        if (step >= 1) return Fmt(x);
        int m = (int)(Math.Max(0, x) / 60);
        return $"{m}:{(Math.Max(0, x) - m * 60).ToString(step < 0.5 ? "00.00" : "00.0")}";
    }

    double trimA = -1, trimB = -1; bool trimEdit;
    // обрезка: вне меток затемнено, между метками ярче
    public void SetTrim(double a, double b, bool edit)
    {
        if (trimA == a && trimB == b && trimEdit == edit) return;
        trimA = a; trimB = b; trimEdit = edit; InvalidateVisual();
    }

    double loopA = -1, loopB = -1;
    // петля: доли дорожки 0..1; -1 — петли нет
    public void SetLoop(double a, double b) { if (loopA == a && loopB == b) return; loopA = a; loopB = b; InvalidateVisual(); }

    bool fill = true;
    Geometry shape;
    Size shapeSize;

    // стиль: true — плавная заливка, false — столбики
    public bool Fill { get => fill; set { if (fill == value) return; fill = value; InvalidateVisual(); } }

    // контур считаем один раз на размер и дорожку, а не на каждый кадр
    Geometry Shape(double w, double h)
    {
        if (shape != null && shapeSize.Width == w && shapeSize.Height == h) return shape;
        double x0 = 8, x1 = w - 8, mid = h / 2, amp = h / 2 - 5;
        int cols = Math.Max(2, (int)((x1 - x0) / 2));
        var v = new double[cols];
        for (int i = 0; i < cols; i++)
        {
            int a = (int)((double)i / cols * peaks.Length);
            int b = Math.Max(a + 1, (int)((double)(i + 1) / cols * peaks.Length));
            float m = 0;
            for (int k = a; k < b && k < peaks.Length; k++) m = Math.Max(m, peaks[k]);
            v[i] = m;
        }
        // скользящее среднее — убирает зубцы
        var s = new double[cols];
        for (int i = 0; i < cols; i++)
        {
            double sum = 0; int n = 0;
            for (int k = Math.Max(0, i - 2); k <= Math.Min(cols - 1, i + 2); k++) { sum += v[k]; n++; }
            s[i] = Math.Max(1.5, sum / n * amp);
        }
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x0, mid - s[0]), true, true);
            for (int i = 1; i < cols; i++) c.LineTo(new Point(x0 + (double)i / (cols - 1) * (x1 - x0), mid - s[i]), true, true);
            for (int i = cols - 1; i >= 0; i--) c.LineTo(new Point(x0 + (double)i / (cols - 1) * (x1 - x0), mid + s[i]), true, true);
        }
        g.Freeze();
        shape = g; shapeSize = new Size(w, h);
        return g;
    }

    public double Progress
    {
        get => progress;
        set { value = Math.Clamp(value, 0, 1); if (Math.Abs(value - progress) < 0.0004) return; progress = value; InvalidateVisual(); }
    }

    static string Fmt(double sec)
    {
        int t = (int)Math.Max(0, Math.Round(sec));
        return t >= 3600 ? $"{t / 3600}:{t / 60 % 60:00}:{t % 60:00}" : $"{t / 60}:{t % 60:00}";
    }

    FormattedText Text(string s, double size, Brush b, bool bold = false) =>
        new(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(new Uri("pack://application:,,,/"), "./Fonts/#Onest"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // подпись на мягкой плашке, читается и поверх заливки
    void Label(DrawingContext dc, string text, double x, double y, Brush fg, bool bold = false)
    {
        var ft = Text(text, 14, fg, bold);
        dc.DrawText(ft, new Point(x + 6, y + 1));
    }

    static Brush Faded(Brush src, double opacity) { var b = src.Clone(); b.Opacity = opacity; return b; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        // заливка лежит прямо на стекле, без серой подложки; прозрачный прямоугольник нужен, чтобы ловить мышь
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        // слева полоса для подписей уровней: волна их не загораживает
        double G = Gutter;
        w -= G;
        dc.PushTransform(new TranslateTransform(G, 0));
        double axisH = axis ? 24 : 0, H = h - axisH;
        double mid = H / 2, px = Math.Clamp(progress, 0, 1) * w;
        var muted = Theme.Get("Muted");
        // красная зона затухания: сама волна в конце трека красная (без плашки), граница — тонкая пунктирная линия
        double fadeX = (duration > 1 && fadeSec > 0) ? w * (1 - Math.Min(fadeSec, duration * 0.4) / duration) : -1;
        var redWave = Faded(Theme.Get("Rec"), Theme.IsLight ? 0.8 : 0.85);
        if (axis && peaks != null)
        {
            // уровни «громко» / «средне»: пунктир сверху и по центру верхней половины
            var dash = new Pen(Faded(muted, 0.45), 1) { DashStyle = new DashStyle(new double[] { 3, 4 }, 0) };
            double amp = H / 2 - 5;
            foreach (var lv in new[] { 1.0, 0.5 })
                foreach (var sg in new[] { -1, 1 })
                {
                    double y = mid + sg * lv * amp;
                    dc.DrawLine(dash, new Point(8, y), new Point(w - 8, y));
                }
        }
        if (peaks == null)
        {
            dc.DrawRectangle(Rest, null, new Rect(8, mid - 1.5, Math.Max(0, w - 16), 3));
            if (px > 8) dc.DrawRectangle(Played, null, new Rect(8, mid - 1.5, px - 8, 3));
        }
        else if (fill)
        {
            // плавная зеркальная заливка: непрочитанная часть светлая, проигранная — акцентная
            var g = Shape(w, H);
            dc.DrawGeometry(Rest, null, g);
            if (fadeX >= 0)
            {
                dc.PushClip(new RectangleGeometry(new Rect(fadeX, 0, w - fadeX, H)));
                dc.DrawGeometry(redWave, null, g);
                dc.Pop();
            }
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, px, H)));
            dc.DrawGeometry(Played, null, g);
            dc.Pop();
        }
        else
        {
            const double bar = 3, gap = 1;
            int count = (int)((w - 16) / (bar + gap));
            for (int i = 0; i < count; i++)
            {
                int a = (int)((double)i / count * peaks.Length);
                int b = Math.Max(a + 1, (int)((double)(i + 1) / count * peaks.Length));
                float v = 0;
                for (int k = a; k < b && k < peaks.Length; k++) v = Math.Max(v, peaks[k]);
                double bh = Math.Max(2, v * (H - 10));
                double x = 8 + i * (bar + gap);
                dc.DrawRoundedRectangle(x + bar / 2 <= px ? Played : (fadeX >= 0 && x >= fadeX ? redWave : Rest), null, new Rect(x, mid - bh / 2, bar, bh), 1, 1);
            }
        }
        if (fadeX >= 0)
        {
            var edge = new Pen(Faded(Theme.Get("Rec"), 0.8), 1.5) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            dc.DrawLine(edge, new Point(fadeX, 0), new Point(fadeX, H));
        }
        if (Loading && peaks == null)
        {
            var ft = Text("считаем волну…", 15, Rest);
            dc.DrawText(ft, new Point(Math.Max(8, (w - ft.Width) / 2), mid - ft.Height / 2));
        }
        if (loopA >= 0 && loopB > loopA)
        {
            var lp = new SolidColorBrush(Color.FromArgb(0x55, ((SolidColorBrush)Played).Color.R, ((SolidColorBrush)Played).Color.G, ((SolidColorBrush)Played).Color.B));
            dc.DrawRoundedRectangle(lp, new Pen(Played, 1.5), new Rect(loopA * w, 0, Math.Max(3, (loopB - loopA) * w), H), 6, 6);
        }
        // обрезка: затемняем всё вне меток «начало» и «конец», между ними подсвечиваем
        if (duration > 1 && (trimA >= 0 || trimB > 0))
        {
            double x1 = trimA >= 0 ? Math.Min(w, trimA / duration * w) : 0;
            double x2 = trimB > 0 ? Math.Min(w, trimB / duration * w) : w;
            var dim = new SolidColorBrush(Color.FromArgb((byte)(Theme.IsLight ? 0x99 : 0xBB), 0x08, 0x0A, 0x0E));
            if (x1 > 0) dc.DrawRectangle(dim, null, new Rect(0, 0, x1, H));
            if (x2 < w) dc.DrawRectangle(dim, null, new Rect(x2, 0, w - x2, H));
            if (Theme.Get("Accent") is SolidColorBrush ab)
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x2A, ab.Color.R, ab.Color.G, ab.Color.B)), null, new Rect(x1, 0, Math.Max(0, x2 - x1), H));
            if (trimA >= 0) dc.DrawLine(new Pen(Theme.Get("Accent"), 3), new Point(x1, 0), new Point(x1, H));
            if (trimB > 0) dc.DrawLine(new Pen(Theme.Get("RecText"), 3), new Point(x2, 0), new Point(x2, H));
            if (axis)
            {
                if (trimA >= 0)
                {
                    dc.DrawRoundedRectangle(Theme.Get("Accent"), null, new Rect(x1, 0, 82, 22), 7, 7);
                    var st = Text("НАЧАЛО", 13, Theme.Get("OnAccent"), true);
                    dc.DrawText(st, new Point(x1 + 41 - st.Width / 2, 11 - st.Height / 2));
                }
                if (trimB > 0)
                {
                    dc.DrawRoundedRectangle(Theme.Get("RecText"), null, new Rect(x2 - 70, 0, 70, 22), 7, 7);
                    var et = Text("КОНЕЦ", 13, Theme.Get("OnAccent"), true);
                    dc.DrawText(et, new Point(x2 - 35 - et.Width / 2, 11 - et.Height / 2));
                }
            }
        }
        // метки: тонкая линия на всю высоту и флажок с номером
        if (cues != null && duration > 1)
            for (int i = 0; i < cues.Length; i++)
            {
                if (cues[i] < 0) continue;
                double cx = cues[i] / duration * w;
                dc.DrawLine(new Pen(Faded(Theme.Get("Text"), 0.75), 1.5), new Point(cx, 0), new Point(cx, H));
                if (!axis) continue;
                dc.DrawRoundedRectangle(Theme.Get("Text"), null, new Rect(cx - 12, 0, 24, 22), 7, 7);
                var nt = Text((i + 1).ToString(), 14, Theme.Get("OnText"), true);
                dc.DrawText(nt, new Point(cx - nt.Width / 2, 11 - nt.Height / 2));
            }
        // игла: тёмная окантовка + светлая полоса, видна и на заливке, и на фоне
        double nx = Math.Clamp(px, 3, w - 3);
        if (peaks != null) dc.DrawRectangle(Theme.Get("OnAccent"), null, new Rect(nx - 3.5, 0, 7, H));
        dc.DrawRectangle(Needle, null, new Rect(nx - 2, 1, 4, H - 2));
        if (axis)
        {
            double amp = H / 2 - 5;
            Label(dc, "громко", 2 - G, mid - amp - 8, Theme.Get("Muted"));
            Label(dc, "средне", 2 - G, mid - amp * 0.5 - 8, Theme.Get("Muted"));
            if (duration > 1 && fadeSec > 0)
            {
                var fadeLbl = $"затухание {Math.Round(Math.Min(fadeSec, duration * 0.4))} с";
                var ft = Text(fadeLbl, 14, Theme.Get("RecText"), true);
                double fx = w * (1 - Math.Min(fadeSec, duration * 0.4) / duration);
                if (w - fx > ft.Width + 20) Label(dc, fadeLbl, Math.Max(fx + 6, w - ft.Width - 24), 2, Theme.Get("RecText"), true);
            }
        }
        if (axis && duration > 1)
        {
            // шкала времени: шаг подбираем так, чтобы подписи не слипались
            double[] steps = { 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300 };
            double step = steps.FirstOrDefault(st => duration / st <= Math.Max(2, w / 70), 600);
            double first = Math.Ceiling(timeOffset / step - 1e-9) * step - timeOffset; // метки кратны шагу по времени трека
            for (double t = first; t <= duration + 0.01; t += step)
            {
                double tx = t / duration * w;
                dc.DrawRectangle(muted, null, new Rect(Math.Min(tx, w - 1), H + 2, 1, 4));
                var ft = Text(FmtT(t + timeOffset, step), 14, muted);
                dc.DrawText(ft, new Point(Math.Clamp(tx - ft.Width / 2, 0, Math.Max(0, w - ft.Width)), H + 6));
            }
            // пузырёк у иглы: сколько прошло и сколько осталось
            if (showBubble)
            {
            double bp = progress * duration, ba = !trimEdit && trimA > 0 ? trimA : 0, bb = !trimEdit && trimB > 0 ? trimB : duration;
            var bt = Text($"{Fmt(Math.Max(0, bp - ba))}  ·  осталось {Fmt(Math.Max(0, bb - bp))}", 15, Theme.Get("OnAccent"), true);
            double bw = bt.Width + 18, bx = Math.Clamp(nx - bw / 2, 2, Math.Max(2, w - bw - 2)), by = H - 28;
            dc.DrawRoundedRectangle(Played, null, new Rect(bx, by, bw, 24), 12, 12);
            dc.DrawText(bt, new Point(bx + 9, by + 12 - bt.Height / 2));
            }
        }
        dc.Pop();
    }

    double Gutter => axis ? 58 : 0;

    double Frac(MouseEventArgs e) => Math.Clamp((e.GetPosition(this).X - Gutter) / Math.Max(1, ActualWidth - Gutter), 0, 1);

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        int c = CueAt(e.GetPosition(this));
        if (c >= 0) { CueRemove?.Invoke(c); e.Handled = true; }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        int c = CueAt(e.GetPosition(this));
        if (c >= 0) { CueJump?.Invoke(c); e.Handled = true; return; }
        dragging = true;
        CaptureMouse();
        var f = Frac(e);
        Progress = f;
        Scrub?.Invoke(f, false);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        Scrub?.Invoke(progress, true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { dragging = false; ReleaseMouseCapture(); Scrub?.Invoke(progress, true); return; }
        var f = Frac(e);
        Progress = f;
        Scrub?.Invoke(f, false);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        ReleaseMouseCapture();
        Scrub?.Invoke(Frac(e), true);
    }
}
