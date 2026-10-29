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

    static void Load()
    {
        if (all != null) return;
        try { all = JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(FilePath)) ?? new(); }
        catch (FileNotFoundException) { all = new(); }
        catch (DirectoryNotFoundException) { all = new(); }
        catch
        {
            // повреждённый файл не затираем молча: оставляем копию
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
            all = new();
        }
    }

    // метки трека: секунды, -1 — метки нет
    public static double[] Get(string path)
    {
        Load();
        var r = new double[Count];
        Array.Fill(r, -1.0);
        if (all.TryGetValue(Key(path), out var v))
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

    public static void Save(string path, double[] cues)
    {
        Load();
        var k = Key(path);
        if (cues.All(c => c < 0)) all.Remove(k); else all[k] = (double[])cues.Clone();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }
}
