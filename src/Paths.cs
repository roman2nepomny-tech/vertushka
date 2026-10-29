using System.IO;

namespace Vertushka;

// Папки программы. До версии 0.5.0 они назывались «Моя вертушка»: при первом запуске переносим на новое имя.
public static class Paths
{
    const string OldName = "Моя вертушка";
    const string NewName = "Vertushka";

    public static readonly string Music;
    public static readonly string AppData;
    // папка с проектами (раньше «Мероприятия»); не удалось переименовать — остаётся старое имя
    public static readonly string ProjectsDirName;

    static Paths()
    {
        // для проверок: VERTUSHKA_TEST_ROOT — отдельные папки вместо настоящих «Музыка» и AppData
        var test = Environment.GetEnvironmentVariable("VERTUSHKA_TEST_ROOT");
        if (!string.IsNullOrEmpty(test))
        {
            Music = System.IO.Path.Combine(test, "Music", NewName);
            AppData = System.IO.Path.Combine(test, "AppData", NewName);
            Directory.CreateDirectory(Music); Directory.CreateDirectory(AppData);
            ProjectsDirName = "Проекты";
            return;
        }
        Music = Resolve(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        AppData = Resolve(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        ProjectsDirName = "Проекты";
        var oldDir = System.IO.Path.Combine(Music, "Мероприятия");
        var newDir = System.IO.Path.Combine(Music, ProjectsDirName);
        if (Directory.Exists(oldDir) && !Directory.Exists(newDir))
        {
            try { Directory.Move(oldDir, newDir); }
            catch { ProjectsDirName = "Мероприятия"; }
        }
    }

    // переносим старую папку на новое имя; не вышло (файлы заняты) — работаем со старой, ничего не теряя
    static string Resolve(string parent)
    {
        var old = System.IO.Path.Combine(parent, OldName);
        var neu = System.IO.Path.Combine(parent, NewName);
        if (Directory.Exists(old) && !Directory.Exists(neu))
        {
            try { Directory.Move(old, neu); }
            catch { return old; }
        }
        return neu;
    }
}
