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
