using System.IO;
using System.Text.Json;

namespace Vertushka;

public class Settings
{
    public double Volume { get; set; } = 0.8;
    // затухание в конце трека: в последние N секунд трек затихает, а следующий плавно нарастает
    public int Crossfade { get; set; } = 6;
    public bool FadeEnabled { get; set; } = true;
    public int FadeVersion { get; set; }
    public bool Topmost { get; set; } = true;
    public int Mic { get; set; } = 0;
    public bool RecMusic { get; set; } = false;
    public string LastSection { get; set; }
    public string Event { get; set; }
    public bool Compact { get; set; }
    public bool Cards { get; set; }
    // оформление: dark / light / auto (по Windows); в зале по умолчанию тёмное
    public string Theme { get; set; } = "dark";
    public int LoopLen { get; set; } = 8;
    // выход для предпрослушивания: имя звукового устройства (номера сдвигаются при подключении USB), пусто — не выбран
    public string PreviewDeviceName { get; set; }
    // метки (обычные и «начало/конец» обрезки) прилипают к ближайшему самому тихому месту
    public bool SnapQuiet { get; set; } = true;
    public bool WaveBars { get; set; }
    public bool MiniOpen { get; set; }
    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }
    public double JingleVolume { get; set; } = 0.8;
    public Dictionary<string, string> LastByEvent { get; set; } = new();

    static string FilePath => System.IO.Path.Combine(Paths.AppData, "settings.json");

    public static Settings Load() => Migrate(LoadRaw());

    // до 0.5.0 «переход» 0 с значил «выключено», по умолчанию было 3 с: теперь это переключатель и 6 с
    static Settings Migrate(Settings s)
    {
        if (s.FadeVersion < 1)
        {
            s.FadeVersion = 1;
            if (s.Crossfade <= 0) { s.FadeEnabled = false; s.Crossfade = 6; }
            else { s.FadeEnabled = true; if (s.Crossfade == 3) s.Crossfade = 6; }
        }
        s.Crossfade = Math.Clamp(s.Crossfade, 1, 15);
        if (s.LoopLen < 1 || s.LoopLen > 64) s.LoopLen = 8;
        return s;
    }

    static Settings LoadRaw()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings { FadeVersion = 1 }; }
        catch (FileNotFoundException) { return new Settings { FadeVersion = 1 }; }
        catch (DirectoryNotFoundException) { return new Settings { FadeVersion = 1 }; }
        catch
        {
            // файл есть, но испорчен: оставляем копию, чтобы следующее сохранение его не затёрло
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
            return new Settings { FadeVersion = 1 };
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, FilePath, true); // сначала во временный файл, чтобы сбой не испортил настройки
        }
        catch { }
    }
}
