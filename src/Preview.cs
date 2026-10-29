using NAudio.Wave;

namespace Vertushka;

// Предпрослушивание: свой плеер на выбранном выходе (наушники), основной звук не трогает.
public sealed class Preview : IDisposable
{
    AudioFileReader reader;
    WaveOutEvent output;

    public bool Active => output != null;
    public bool Ended { get; private set; }
    public Exception Error { get; private set; }
    public TimeSpan Position => reader?.CurrentTime ?? TimeSpan.Zero;

    public static List<string> Devices()
    {
        var l = new List<string>();
        for (int i = 0; i < WaveOut.DeviceCount; i++) l.Add(WaveOut.GetCapabilities(i).ProductName);
        return l;
    }

    // номер устройства по имени, -1 — не найдено
    public static int Find(string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        var d = Devices();
        return d.IndexOf(name);
    }

    public void Start(string path, int device, float volume)
    {
        Dispose();
        Ended = false;
        Error = null;
        try
        {
            reader = new AudioFileReader(path) { Volume = Math.Clamp(volume, 0f, 1f) };
            var o = new WaveOutEvent { DeviceNumber = device, DesiredLatency = 300, NumberOfBuffers = 3 };
            output = o;
            o.Init(reader);
            // события от прежнего (уже остановленного) вывода игнорируем
            o.PlaybackStopped += (s, e) => { if (ReferenceEquals(o, output)) { Error = e.Exception; Ended = true; } };
            output.Play();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { output?.Stop(); } catch { }
        output?.Dispose();
        reader?.Dispose();
        output = null;
        reader = null;
    }
}
