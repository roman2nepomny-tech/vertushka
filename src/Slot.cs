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
    public bool FromNext;          // включён из списка «Далее»
    public volatile bool Ended;             // звук закончился (дошли до конца данных или устройство остановилось)
    public volatile Exception StopError;   // звуковое устройство остановилось с ошибкой (а не трек дошёл до конца)
    readonly object gate = new();

    public Slot(Song song)
    {
        Song = song;
        Reader = new AudioFileReader(song.Path);
        try
        {
            Out = new WaveOutEvent { DesiredLatency = 120 };
            Out.Init(new LockedWave(Reader, gate));
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

    sealed class LockedWave : IWaveProvider
    {
        readonly IWaveProvider src; readonly object gate;
        public LockedWave(IWaveProvider src, object gate) { this.src = src; this.gate = gate; }
        public WaveFormat WaveFormat => src.WaveFormat;
        public int Read(byte[] buffer, int offset, int count) { lock (gate) return src.Read(buffer, offset, count); }
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
