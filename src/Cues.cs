using System.IO;
using System.Text.Json;

namespace Vertushka;

// Метки на волне: до 4 мест в треке (секунды). Хранятся по файлу, между запусками.
public static class Cues
{
    public const int Count = 4;

    static Dictionary<string, double[]> all;

    static string FilePath => System.IO.Path.Combine(Paths.AppData, "cues.json");

    static string Key(string path)
    {
        try { var fi = new FileInfo(path); return fi.FullName.ToLowerInvariant() + "|" + fi.Length; }
        catch { return path.ToLowerInvariant(); }
    }

    // false — файл сейчас занят (антивирус, другая программа): не считаем его пустым, чтобы потом не затереть
    static bool Load()
    {
        if (all != null) return true;
        try { all = JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(FilePath)) ?? new(); return true; }
        catch (FileNotFoundException) { all = new(); return true; }
        catch (DirectoryNotFoundException) { all = new(); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        catch
        {
            // повреждённый файл не затираем молча: оставляем копию
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
            all = new();
            return true;
        }
    }

    // метки трека: секунды, -1 — метки нет
    public static double[] Get(string path)
    {
        bool loaded = Load();
        var r = new double[Count];
        Array.Fill(r, -1.0);
        if (loaded && all.TryGetValue(Key(path), out var v))
            for (int i = 0; i < Count && i < v.Length; i++) r[i] = v[i];
        return r;
    }

    // забрать метки трека (и убрать их из файла): перед переносом или удалением трека
    public static double[] Take(string path)
    {
        var c = Get(path);
        Save(path, Enumerable.Repeat(-1.0, Count).ToArray());
        return c;
    }

    public static bool Save(string path, double[] cues)
    {
        if (!Load()) return false;
        var k = Key(path);
        if (cues.All(c => c < 0)) all.Remove(k); else all[k] = (double[])cues.Clone();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all));
            File.Move(tmp, FilePath, true);
            return true;
        }
        catch { return false; }
    }
}
