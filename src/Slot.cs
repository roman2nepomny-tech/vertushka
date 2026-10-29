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
            Out = new WaveOutEvent { DesiredLatency = 300, NumberOfBuffers = 3 }; // запас на «икоту» Windows при открытии блютуз-устройств (120 мс было мало)
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
    // поэтому звуковой поток читает и выбрасывает кусочек звука перед нужным местом, а сам звук нарастает за 30 мс — без щелчка и шума.
    // Само чтение делает только звуковой поток: mp3 читает Media Foundation, а он работает с файлом лишь из того потока, где идёт чтение
    // (перемотка и чтение из окна роняли программу: InvalidCastException на IMFSourceReader).
    public void SeekClean(double sec)
    {
        sec = Math.Max(0, sec);
        lock (gate)
        {
            Reader.CurrentTime = TimeSpan.FromSeconds(sec);
            locked.PreRoll = Math.Min(sec, 0.3);
            locked.ArmRamp((int)(0.03 * Reader.WaveFormat.SampleRate));
        }
    }

    sealed class LockedWave : IWaveProvider
    {
        readonly IWaveProvider src; readonly object gate;
        int rampTotal, rampDone; // кадры плавного нарастания громкости после перемотки
        public double PreRoll;   // секунд звука перед местом перемотки, которые надо прочитать и выбросить (под замком)
        public LockedWave(IWaveProvider src, object gate) { this.src = src; this.gate = gate; }
        public WaveFormat WaveFormat => src.WaveFormat;

        public void ArmRamp(int frames) { lock (gate) { rampTotal = Math.Max(0, frames); rampDone = 0; } }

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (gate)
            {
                if (PreRoll > 0 && src is AudioFileReader ar)
                {
                    double pre = PreRoll; PreRoll = 0;
                    try
                    {
                        ar.CurrentTime = ar.CurrentTime - TimeSpan.FromSeconds(pre);
                        int align = ar.WaveFormat.BlockAlign;
                        int left = (int)(pre * ar.WaveFormat.AverageBytesPerSecond);
                        left -= left % align;
                        var tmp = new byte[Math.Max(align, Math.Min(left, 1 << 18))];
                        while (left > 0)
                        {
                            int k = ar.Read(tmp, 0, Math.Min(tmp.Length, left));
                            if (k <= 0) break;
                            left -= k;
                        }
                    }
                    catch (Exception) { } // не вышло — играем с места перемотки как есть
                }
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
