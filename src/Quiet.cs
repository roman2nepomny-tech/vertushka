using NAudio.Wave;

namespace Vertushka;

// Поиск самого тихого места рядом с заданной секундой: метка «прилипает» к паузе, а не режет звук посередине.
public static class Quiet
{
    public const double Radius = 1.5; // секунд в каждую сторону

    public static double Find(string path, double pos, double radius = Radius)
    {
        using var r = new AudioFileReader(path);
        double total = r.TotalTime.TotalSeconds;
        double a = Math.Max(0, pos - radius), b = Math.Min(total, pos + radius);
        if (b - a < 0.1) return pos;
        int ch = r.WaveFormat.Channels, sr = r.WaveFormat.SampleRate;
        int hop = Math.Max(1, sr / 100); // блок 10 мс
        r.CurrentTime = TimeSpan.FromSeconds(a);
        var buf = new float[hop * ch];
        var energy = new List<double>(); // сумма квадратов по блокам
        int frames = (int)((b - a) * sr);
        for (int done = 0; done + hop <= frames; done += hop)
        {
            int n = r.Read(buf, 0, buf.Length);
            if (n < buf.Length) break;
            double s = 0;
            for (int k = 0; k < n; k++) s += buf[k] * buf[k];
            energy.Add(s);
        }
        const int W = 5; // громкость считаем по окну 50 мс со сдвигом 10 мс: ищем настоящую паузу, а не провал внутри волны
        if (energy.Count < W) return pos;
        double best = double.MaxValue, bestT = pos;
        for (int i = 0; i + W <= energy.Count; i++)
        {
            double sum = 0;
            for (int k = i; k < i + W; k++) sum += energy[k];
            double t = a + (i + W / 2.0) * hop / (double)sr;
            double score = Math.Sqrt(sum / (W * hop * ch)) + 1e-6 * Math.Abs(t - pos); // при равенстве — ближайшее к нажатию
            if (score < best) { best = score; bestT = t; }
        }
        return Math.Round(Math.Clamp(bestT, 0, Math.Max(0, total - 0.1)), 2);
    }
}
