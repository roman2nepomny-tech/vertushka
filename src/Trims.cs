using System.IO;
using System.Text.Json;

namespace Vertushka;

// Обрезка трека без изменения файла: «начало» и «конец» в секундах, -1 — не задано. Хранится по файлу (путь + размер).
public static class Trims
{
    static Dictionary<string, double[]> all;
    static string FilePath => System.IO.Path.Combine(Paths.AppData, "trims.json");

    static string Key(string path)
    {
        try { var fi = new FileInfo(path); return fi.FullName.ToLowerInvariant() + "|" + fi.Length; }
        catch { return path.ToLowerInvariant(); }
    }

    // false — файл сейчас занят: не считаем его пустым, чтобы потом не затереть
    static bool Load()
    {
        if (all != null) return true;
        try { all = JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(FilePath)) ?? new(); return true; }
        catch (FileNotFoundException) { all = new(); return true; }
        catch (DirectoryNotFoundException) { all = new(); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        catch
        {
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
            all = new();
            return true;
        }
    }

    // {начало, конец}
    public static double[] Get(string path)
    {
        if (Load() && all.TryGetValue(Key(path), out var v) && v.Length >= 2) return new[] { v[0], v[1] };
        return new[] { -1.0, -1.0 };
    }

    public static bool Has(string path) { var t = Get(path); return t[0] >= 0 || t[1] > 0; }

    public static bool Save(string path, double start, double end)
    {
        if (!Load()) return false;
        var k = Key(path);
        if (start < 0 && end <= 0) all.Remove(k); else all[k] = new[] { start, end };
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

    // забрать обрезку (и убрать из файла): перед переносом или удалением трека
    public static double[] Take(string path)
    {
        var t = Get(path);
        Save(path, -1, -1);
        return t;
    }
}
