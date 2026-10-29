using NAudio.Wave;

namespace Vertushka;

// Один играющий трек с плавным изменением громкости (для переходов и затухания).
public class Slot : IDisposable
{
    public readonly AudioFileReader Reader;
    public readonly WaveOutEvent Out;
    public readonly Song Song;
    public float Factor = 1f, Target = 1f, Step;
    public Action OnZero;
    public bool NextTriggered;
    public double TrimStart = -1, TrimEnd = -1;   // обрезка (сек), -1 — не задана
    public bool FromNext;          // включён из списка «Далее»
    public volatile bool Ended;             // звук закончился (дошли до конца данных или устройство остановилось)
    public volatile Exception StopError;   // звуковое устройство остановилось с ошибкой (а не трек дошёл до конца)
    readonly object gate = new();
    readonly LockedWave locked;

    public Slot(Song song)
    {
        Song = song;
        Reader = new AudioFileReader(song.Path) { Volume = 0f }; // тихо, пока не выставлена громкость (иначе первые доли секунды играют на полную)
        try
        {
            Out = new WaveOutEvent { DesiredLatency = 120 };
            locked = new LockedWave(Reader, gate);
            Out.Init(locked);
            Out.PlaybackStopped += (s, e) => { StopError = e.Exception; Ended = true; };
        }
        catch
        {
            Out?.Dispose();
            Reader.Dispose();
            throw;
        }
    }

    // перемотка под замком: звуковой поток в это время читает тот же файл
    public void Seek(TimeSpan t) { lock (gate) Reader.CurrentTime = t; }

    // перемотка в «чистое» начало (метка «Начало» обрезки, «назад»): после прыжка декодер (особенно mp3) сначала выдаёт мусор,
    // поэтому читаем и выбрасываем кусочек звука перед нужным местом, а сам звук нарастает за 30 мс — без щелчка и шума
    public void SeekClean(double sec)
    {
        sec = Math.Max(0, sec);
        lock (gate)
        {
            double pre = Math.Min(sec, 0.3);
            Reader.CurrentTime = TimeSpan.FromSeconds(sec - pre);
            int align = Reader.WaveFormat.BlockAlign;
            int left = (int)(pre * Reader.WaveFormat.AverageBytesPerSecond);
            left -= left % align;
            var buf = new byte[Math.Max(align, Math.Min(left, 1 << 18))];
            while (left > 0)
            {
                int n = Reader.Read(buf, 0, Math.Min(buf.Length, left));
                if (n <= 0) break;
                left -= n;
            }
        }
        locked.ArmRamp((int)(0.03 * Reader.WaveFormat.SampleRate));
    }

    sealed class LockedWave : IWaveProvider
    {
        readonly IWaveProvider src; readonly object gate;
        int rampTotal, rampDone; // кадры плавного нарастания громкости после перемотки
        public LockedWave(IWaveProvider src, object gate) { this.src = src; this.gate = gate; }
        public WaveFormat WaveFormat => src.WaveFormat;

        public void ArmRamp(int frames) { lock (gate) { rampTotal = Math.Max(0, frames); rampDone = 0; } }

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (gate)
            {
                int n = src.Read(buffer, offset, count);
                if (n > 0 && rampDone < rampTotal && src.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat && src.WaveFormat.BitsPerSample == 32)
                {
                    int ch = src.WaveFormat.Channels;
                    int frames = n / (4 * ch);
                    for (int f = 0; f < frames && rampDone < rampTotal; f++, rampDone++)
                    {
                        float g = (float)rampDone / rampTotal;
                        for (int c = 0; c < ch; c++)
                        {
                            int i = offset + (f * ch + c) * 4;
                            BitConverter.TryWriteBytes(new Span<byte>(buffer, i, 4), BitConverter.ToSingle(buffer, i) * g);
                        }
                    }
                }
                return n;
            }
        }
    }

    public void FadeTo(float target, double seconds)
    {
        Target = target;
        Step = seconds <= 0 ? 1000f : (float)(Math.Abs(target - Factor) / seconds); // за секунду
    }

    long lastTick;

    public void Tick(float master)
    {
        long now = Environment.TickCount64;
        double dt = lastTick == 0 ? 0.05 : Math.Min(0.5, (now - lastTick) / 1000.0); // секунды с прошлого такта
        lastTick = now;
        if (Factor != Target)
        {
            float st = Step * (float)dt;
            if (Factor < Target) Factor = Math.Min(Target, Factor + st);
            else Factor = Math.Max(Target, Factor - st);
            if (Factor == 0f && OnZero != null)
            {
                var a = OnZero;
                OnZero = null;
                Reader.Volume = 0f;
                a();
                return;
            }
        }
        Reader.Volume = Math.Clamp(master * Factor, 0f, 1f);
    }

    bool disposed;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Out.Stop(); } catch { }
        Out.Dispose();
        lock (gate) Reader.Dispose(); // звуковой поток мог ещё читать файл
    }
}
