using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vertushka;

public class Song : INotifyPropertyChanged
{
    bool _isPlaying;

    public string Name { get; set; }
    public string Path { get; set; }
    public string Section { get; set; }
    int _num;

    public int Num
    {
        get => _num;
        set
        {
            if (_num == value) return;
            _num = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Num)));
        }
    }

    int _nextPos;

    // место в списке «Далее» (1, 2, …); 0 — трека там нет
    public int NextPos
    {
        get => _nextPos;
        set
        {
            if (_nextPos == value) return;
            _nextPos = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NextPos)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NextBadge)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNext)));
        }
    }

    public bool HasNext => _nextPos > 0;
    public string NextBadge => _nextPos > 0 ? $"Далее {_nextPos}" : "";

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying)));
        }
    }

    // карточка ушла из списка — обложку для неё больше не ищем
    public bool Discarded { get; set; }

    double _progress;
    bool _paused;

    // ход играющего трека (0..1) и пауза — для полосы и значка на карточке
    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) < 0.001) return;
            _progress = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
        }
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Paused)));
        }
    }

    Brush _cover;
    bool _coverAsked, _hasCover;

    // «Обложка» для карточки: сначала мягкий градиент, потом, если у файла есть своя, она подменяет его
    public Brush CoverBrush
    {
        get
        {
            if (_cover == null)
            {
                _cover = Covers.Gradient(Name);
                if (!_coverAsked) { _coverAsked = true; _ = LoadCoverAsync(); }
            }
            return _cover;
        }
    }

    public bool HasCover
    {
        get => _hasCover;
        private set
        {
            _hasCover = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCover)));
        }
    }

    async Task LoadCoverAsync()
    {
        var img = await Task.Run(() => Covers.Thumbnail(Path, () => Discarded));
        if (img == null) return;
        _cover = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        _cover.Freeze();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverBrush)));
        HasCover = true;
    }

    public event PropertyChangedEventHandler PropertyChanged;
}

public static class Covers
{
    static readonly SemaphoreSlim gate = new(2);

    // цвет от названия: одно и то же название — всегда один и тот же градиент
    public static Brush Gradient(string name)
    {
        int h = 17;
        foreach (char c in name ?? "") h = h * 31 + c;
        double hue = 205 + (uint)h % 30; // графитово-синие оттенки, как в концепте 8
        var b = new LinearGradientBrush(Hsl(hue, 0.30, 0.62), Hsl(hue + 8, 0.34, 0.30), 45);
        b.Freeze();
        return b;
    }

    static Color Hsl(double h, double s, double l)
    {
        h = (h % 360) / 360;
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double F(double t)
        {
            t = (t + 1) % 1;
            return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
        }
        return Color.FromRgb((byte)(F(h + 1.0 / 3) * 255), (byte)(F(h) * 255), (byte)(F(h - 1.0 / 3) * 255));
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    // настоящая миниатюра Windows (обложка из тегов файла); null — если у файла её нет
    public static ImageSource Thumbnail(string path, Func<bool> cancelled)
    {
        gate.Wait();
        IntPtr hbm = IntPtr.Zero;
        IShellItemImageFactory f = null;
        try
        {
            if (cancelled()) return null;
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out f);
            const int thumbnailOnly = 0x8, biggerOk = 0x1;
            if (f.GetImage(new SIZE { cx = 128, cy = 128 }, thumbnailOnly | biggerOk, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return (ImageSource)src;
        }
        catch { return null; }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (f != null) { try { Marshal.ReleaseComObject(f); } catch { } }
            gate.Release();
        }
    }
}

public class SectionItem
{
    public string Name { get; set; }
    public int Count { get; set; }
    // цвет плитки раздела — от названия, как у обложек
    Brush fill;
    public Brush Fill => fill ??= Covers.Gradient(Name);
}
