using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Vertushka;

public partial class MainWindow : Window
{
    static readonly string[] Exts = { ".mp3", ".wav", ".flac", ".m4a", ".wma", ".aac", ".aif", ".aiff" };
    const string RecSection = "Записи";

    readonly string root = Paths.Music;
    const string MainEvent = "Основное";
    string EventsDir => System.IO.Path.Combine(root, Paths.ProjectsDirName);
    // «Основное» лежит как в версии 0.1.0 (прямо в «Моя вертушка»), остальные — в «Проекты\<название>»
    string EventRoot => cfg.Event == null ? root : System.IO.Path.Combine(EventsDir, cfg.Event);
    string SectionsDir => System.IO.Path.Combine(EventRoot, "Разделы");
    string JinglesDir => System.IO.Path.Combine(EventRoot, "Джинглы");
    string SectionDir(string name) => System.IO.Path.Combine(SectionsDir, name);

    readonly Settings cfg = Settings.Load();
    readonly ObservableCollection<Song> songs = new();
    System.ComponentModel.ICollectionView songsView;
    List<Song> queue = new();
    readonly List<Song> upNext = new(); // «Далее»: играет поверх очереди, по порядку
    int qi = -1;
    string shownSection, playingSection;
    Slot cur;
    // «Петля» и «Поклон»
    bool loopOn; double loopStart;
    bool ducking; float duck = 1f;
    Preview preview;
    string previewName = "";
    double[] cueSec = { -1, -1, -1, -1 }; // метки текущего трека
    readonly List<Song> history = new();
    readonly List<Slot> fading = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    bool seeking, loading, ready;
    int saveTick;
    int jinglesActive;
    string statusText = "";
    DateTime statusAt;

    // запись
    bool recording;
    IWaveIn micIn;
    WasapiLoopbackCapture loop;
    WaveFileWriter writer;
    BufferedWaveProvider micBuf, loopBuf;
    MixingSampleProvider mixer;
    System.Threading.Timer pump;
    readonly object recLock = new();
    System.Diagnostics.Stopwatch recWatch;
    long recFrames;
    string recPath;

    public MainWindow()
    {
        InitializeComponent();
        // на ноутбуке экран меньше: окно не больше рабочей области (без панели задач)
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width); Height = Math.Min(Height, wa.Height);
        if (cfg.Event != null && !Directory.Exists(EventRoot)) cfg.Event = null;
        try { EnsureEventDirs(); }
        catch { SetStatus("Папка с музыкой недоступна. Проверьте диск или сеть и перезапустите программу"); }
        UpdateEventUi();

        Topmost = cfg.Topmost; TopTgl.IsChecked = cfg.Topmost;
        VolSlider.Value = cfg.Volume;
        JingleVol.Value = cfg.JingleVolume;
        FadeSlider.Value = cfg.Crossfade;
        SetFadeEnabled(cfg.FadeEnabled);
        MusicTgl.IsChecked = cfg.RecMusic;
        TrackList.ItemsSource = songs;
        songsView = System.Windows.Data.CollectionViewSource.GetDefaultView(songs);
        songsView.Filter = o => string.IsNullOrWhiteSpace(SearchBox.Text)
            || ((Song)o).Name.Contains(SearchBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
        UpdateMicLabel();
        RebuildJingles();
        SetCompact(cfg.Compact);
        SetCards(cfg.Cards);
        SetWaveStyle(!cfg.WaveBars);
        ready = true;
        LoadSections(LastSec());
        if (cfg.MiniOpen) Dispatcher.BeginInvoke(OpenMini);
        WaveformCache.Cleanup();

        Wave.Scrub += Wave_Scrub;
        ZoomWave.Scrub += ZoomScrub;
        TrackList.SelectionChanged += (s, e) => { int n = TrackList.SelectedItems.Count; if (n > 1) SetStatus($"Выбрано треков: {n}. Перетащите их мышью или правый клик"); };
        ZoomWave.MouseWheel += (s, e) => { if (e.Delta > 0) ZoomStep(1); else ZoomStep(-1); e.Handled = true; };
        Wave.CueJump += JumpCue;
        Wave.CueRemove += RemoveCue;
        timer.Tick += (s, e) => Tick();
        timer.Start();
        // файл открыт двойным щелчком / «Открыть с помощью»: кладём в раздел «Из проводника» и включаем
        var openFiles = Environment.GetCommandLineArgs().Skip(1).Where(a => File.Exists(a) && IsAudio(a)).ToList();
        if (openFiles.Count > 0) Loaded += (s, e) => OpenFiles(openFiles);
    }

    const string OpenedSection = "Из проводника";

    async void OpenFiles(List<string> files)
    {
        try
        {
            var dir = SectionDir(OpenedSection);
            Directory.CreateDirectory(dir);
            // такой же файл уже лежит в разделе — второй раз не копируем
            var toCopy = files.Where(f =>
            {
                var d = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(f));
                return !(File.Exists(d) && new FileInfo(d).Length == new FileInfo(f).Length);
            }).ToList();
            if (toCopy.Count > 0) await Task.Run(() => CopyInto(toCopy, dir));
            // если что-то уже играет (сет идёт), музыку не трогаем и раздел не переключаем: файл просто добавлен
            if (cur != null && cur.Out.PlaybackState == PlaybackState.Playing)
            {
                RefreshAll();
                SetStatus($"Добавлено в раздел «{OpenedSection}»: включите, когда нужно");
                return;
            }
            LoadSections(OpenedSection);
            var first = files[0];
            var nm = System.IO.Path.GetFileNameWithoutExtension(first);
            long len = new FileInfo(first).Length;
            var song = songs.FirstOrDefault(x => (x.Name == nm || x.Name.StartsWith(nm + " (", StringComparison.Ordinal)) && new FileInfo(x.Path).Length == len);
            if (song != null) { PlayFromList(song); TrackList.ScrollIntoView(song); }
        }
        catch (Exception ex) { App.LogCrash("OpenFiles", ex); SetStatus("Не получилось открыть файл"); }
    }

    // второй запуск программы передал сюда файлы (или просто «покажись»)
    public void OnExternalOpen(List<string> lines)
    {
        if (closing) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        var files = lines.Where(a => File.Exists(a) && IsAudio(a)).ToList();
        if (files.Count > 0) OpenFiles(files);
    }

    // ---------- разделы и треки ----------

    static bool IsAudio(string f) => Exts.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant());

    const string OrderFile = ".vertushka-order";

    List<string> LoadOrder(string dir)
    {
        try { return File.ReadAllLines(System.IO.Path.Combine(dir, OrderFile)).ToList(); }
        catch { return new List<string>(); }
    }

    void SaveOrder(string section, IEnumerable<Song> list) =>
        WriteOrder(SectionDir(section), list.Select(s => System.IO.Path.GetFileName(s.Path)));

    static void WriteOrder(string dir, IEnumerable<string> names, string file = OrderFile)
    {
        var path = System.IO.Path.Combine(dir, file);
        try
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllLines(path, names);
            File.SetAttributes(path, FileAttributes.Hidden);
        }
        catch { }
    }

    // убрать имя файла из сохранённого порядка раздела (трек перенесли или удалили)
    void ForgetInOrder(string section, string fileName)
    {
        var dir = SectionDir(section);
        var order = LoadOrder(dir);
        if (order.RemoveAll(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)) > 0) WriteOrder(dir, order);
    }

    // Порядок: сначала как расставил вручную, новые файлы — в конце по времени добавления.
    List<Song> ReadSongs(string section)
    {
        var dir = SectionDir(section);
        if (!Directory.Exists(dir)) return new List<Song>();
        var files = Directory.EnumerateFiles(dir).Where(IsAudio)
            .OrderBy(f => File.GetCreationTimeUtc(f)).ThenBy(f => f).ToList();
        var order = LoadOrder(dir);
        if (order.Count > 0)
        {
            var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < order.Count; i++) pos.TryAdd(order[i], i);
            files = files.OrderBy(f => pos.TryGetValue(System.IO.Path.GetFileName(f), out var p) ? p : int.MaxValue).ToList();
        }
        return files
            .Select((f, i) => new Song { Name = System.IO.Path.GetFileNameWithoutExtension(f), Path = f, Section = section, Num = i + 1 })
            .ToList();
    }

    const string SectionsOrderFile = ".vertushka-sections";

    // разделы в порядке, который расставил вручную; новые — в конце по времени создания
    List<string> OrderedSectionDirs()
    {
        var dirs = Directory.Exists(SectionsDir) ? Directory.GetDirectories(SectionsDir).ToList() : new List<string>();
        var order = new List<string>();
        try { order = File.ReadAllLines(System.IO.Path.Combine(SectionsDir, SectionsOrderFile)).ToList(); } catch { }
        var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < order.Count; i++) pos.TryAdd(order[i], i);
        return dirs.OrderBy(d => pos.TryGetValue(System.IO.Path.GetFileName(d), out var p) ? p : int.MaxValue)
            .ThenBy(d => Directory.GetCreationTimeUtc(d)).ThenBy(d => d).ToList();
    }

    void LoadSections(string select = null)
    {
        loading = true;
        var items = OrderedSectionDirs()
            .Select(d => new SectionItem { Name = System.IO.Path.GetFileName(d), Count = Directory.EnumerateFiles(d).Count(IsAudio) })
            .ToList();
        SectionList.ItemsSource = items;
        var pick = items.FirstOrDefault(i => i.Name == (select ?? shownSection)) ?? items.FirstOrDefault();
        SectionList.SelectedItem = pick;
        if (pick != null) SectionList.ScrollIntoView(pick);
        loading = false;
        if (pick != null) ShowSection(pick.Name);
        else
        {
            shownSection = null;
            DiscardSongs();
            SectionTitle.Text = "";
            SectionInfo.Text = "Добавьте раздел кнопкой «+ Новый раздел»";
            EmptyHint.Visibility = Visibility.Collapsed;
        }
    }

    void RefreshAll()
    {
        LoadSections(shownSection);
        if (playingSection != null && playingSection != shownSection) RebuildQueue();
    }

    void ShowSection(string name)
    {
        shownSection = name;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        SetLastSec(name);
        DiscardSongs();
        foreach (var s in ReadSongs(name)) songs.Add(s);
        SectionTitle.Text = name;
        SectionInfo.Text = songs.Count == 0 ? "" : Plural(songs.Count, "трек", "трека", "треков");
        SectionInfo.ToolTip = "Порядок меняется перетаскиванием";
        EmptyHint.Visibility = songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (name == playingSection) RebuildQueue();
        MarkPlaying();
    }

    static string Plural(int n, string one, string few, string many)
    {
        int m10 = n % 10, m100 = n % 100;
        var w = (m10 == 1 && m100 != 11) ? one : (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) ? few : many;
        return $"{n} {w}";
    }

    void RebuildQueue()
    {
        var path = cur?.Song.Path;
        queue = playingSection == shownSection ? songs.ToList() : ReadSongs(playingSection);
        qi = path == null ? -1 : queue.FindIndex(s => s.Path == path);
        MarkPlaying();
    }

    // уходим из раздела: недогруженные обложки старых карточек больше не нужны
    void DiscardSongs()
    {
        foreach (var s in songs) s.Discarded = true;
        songs.Clear();
    }

    void MarkPlaying()
    {
        var p = cur?.Song.Path;
        foreach (var s in songs.Concat(queue))
        {
            s.IsPlaying = p != null && s.Path == p;
            if (!s.IsPlaying) { s.Progress = 0; s.Paused = false; }
        }
        foreach (var s in songs)
            s.NextPos = upNext.FindIndex(u => u.Path == s.Path) + 1;
    }

    void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        if (SectionList.SelectedItem is SectionItem s) ShowSection(s.Name);
    }

    static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    string SafeName(string raw)
    {
        if (raw == null) return null;
        var n = raw.Trim();
        if (n.Length == 0 || n.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) { SetStatus("В названии нельзя использовать символы \\ / : * ? \" < > |"); return null; }
        if (n.EndsWith('.') || n.EndsWith(' ')) { SetStatus("Название не может заканчиваться точкой или пробелом"); return null; }
        if (n.Length > 80) { SetStatus("Слишком длинное название (не больше 80 знаков)"); return null; }
        var head = n.Split('.')[0].Trim().ToUpperInvariant();
        if (Array.IndexOf(ReservedNames, head) >= 0) { SetStatus("Это имя занято Windows, выберите другое"); return null; }
        return n;
    }

    void NewSection_Click(object sender, RoutedEventArgs e)
    {
        var n = SafeName(InputBox.Ask(this, "Новый раздел", "Название раздела:"));
        if (n == null) return;
        if (Directory.Exists(SectionDir(n))) { SetStatus("Такой раздел уже есть"); return; }
        try { Directory.CreateDirectory(SectionDir(n)); }
        catch { SetStatus("Не получилось создать раздел с таким названием"); return; }
        LoadSections(n);
    }

    void SectionList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SectionList.SelectedItem is not SectionItem s) { e.Handled = true; return; }
        var m = new ContextMenu();
        var ren = new MenuItem { Header = "Переименовать" };
        var del = new MenuItem { Header = "Удалить раздел" };
        if (s.Name == RecSection) { ren.IsEnabled = false; del.IsEnabled = false; }
        ren.Click += (a, b) => RenameSection(s.Name);
        del.Click += (a, b) => DeleteSection(s.Name);
        m.Items.Add(ren); m.Items.Add(del);
        SectionList.ContextMenu = m;
    }

    void RenameSection(string old)
    {
        var n = SafeName(InputBox.Ask(this, "Переименовать раздел", "Новое название:", old));
        if (n == null || n == old) return;
        if (Directory.Exists(SectionDir(n))) { SetStatus("Такой раздел уже есть"); return; }
        try
        {
            Directory.Move(SectionDir(old), SectionDir(n));
            if (playingSection == old) playingSection = n;
            if (shownSection == old) shownSection = n;
            LoadSections(n);
        }
        catch { SetStatus("Не получилось: из этого раздела сейчас играет трек. Остановите музыку и повторите"); }
    }

    void DeleteSection(string name)
    {
        var cnt = Directory.EnumerateFiles(SectionDir(name)).Count(IsAudio);
        var r = MessageBox.Show(this, $"Удалить раздел «{name}» вместе с {Plural(cnt, "треком", "треками", "треками")}?\nФайлы уйдут в корзину, их можно будет вернуть.",
            "Удаление раздела", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        try
        {
            FileSystem.DeleteDirectory(SectionDir(name), UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            upNext.RemoveAll(u => u.Section == name);
            if (shownSection == name) shownSection = null;
            if (playingSection == name) { playingSection = null; queue = new List<Song>(); qi = -1; }
            LoadSections();
        }
        catch { SetStatus("Не получилось удалить: из этого раздела сейчас играет трек"); }
    }

    // ---------- проекты ----------

    string EvKey => cfg.Event ?? "";

    string LastSec() => cfg.LastByEvent.TryGetValue(EvKey, out var v) ? v : (cfg.Event == null ? cfg.LastSection : null);

    void SetLastSec(string name)
    {
        cfg.LastByEvent[EvKey] = name;
        if (cfg.Event == null) cfg.LastSection = name;
    }

    void EnsureEventDirs()
    {
        Directory.CreateDirectory(JinglesDir);
        if (cfg.Event == null && !Directory.Exists(SectionsDir))
        {
            foreach (var n in new[] { "Встреча гостей", "Танцы", "Тосты" })
                Directory.CreateDirectory(SectionDir(n));
        }
        Directory.CreateDirectory(SectionDir(RecSection));
    }

    void UpdateEventUi()
    {
        EventText.Text = $"Проект: {cfg.Event ?? MainEvent}  ▾";
        Title = cfg.Event == null ? "Vertushka" : $"Vertushka — {cfg.Event}";
    }

    List<string> EventNames() =>
        Directory.Exists(EventsDir)
            ? Directory.GetDirectories(EventsDir).OrderBy(d => Directory.GetCreationTimeUtc(d)).ThenBy(d => d).Select(System.IO.Path.GetFileName).ToList()
            : new List<string>();

    void ReloadEvent()
    {
        StopAll();
        shownSection = null; playingSection = null; queue = new List<Song>(); qi = -1;
        upNext.Clear();
        EnsureEventDirs();
        UpdateEventUi();
        RebuildJingles();
        LoadSections(LastSec());
        cfg.Save();
    }

    void Event_Click(object sender, RoutedEventArgs e)
    {
        if (recording) { SetStatus("Сначала закончите запись"); return; }
        var m = new ContextMenu();
        foreach (var name in new[] { MainEvent }.Concat(EventNames()))
        {
            var n = name;
            var it = new MenuItem { Header = n, IsCheckable = true, IsChecked = (cfg.Event ?? MainEvent) == n };
            it.Click += (a, b) => SwitchEvent(n);
            m.Items.Add(it);
        }
        m.Items.Add(new Separator { Style = (Style)FindResource("MenuSep") });
        var add = new MenuItem { Header = "+ Новый проект…" };
        add.Click += (a, b) => NewEvent();
        m.Items.Add(add);
        var ren = new MenuItem { Header = "Переименовать…", IsEnabled = cfg.Event != null };
        ren.Click += (a, b) => RenameEvent();
        m.Items.Add(ren);
        var del = new MenuItem { Header = "Удалить проект…", IsEnabled = cfg.Event != null };
        del.Click += (a, b) => DeleteEvent();
        m.Items.Add(del);
        m.PlacementTarget = EventBtn;
        m.Placement = PlacementMode.Bottom;
        m.IsOpen = true;
    }

    void SwitchEvent(string name)
    {
        if (recording) { SetStatus("Сначала закончите запись"); return; }
        var ev = name == MainEvent ? null : name;
        if (ev == cfg.Event) return;
        cfg.Event = ev;
        ReloadEvent();
        SetStatus($"Открыт проект «{name}»");
    }

    bool EventNameTaken(string n) =>
        n.Equals(MainEvent, StringComparison.OrdinalIgnoreCase) || Directory.Exists(System.IO.Path.Combine(EventsDir, n));

    async void NewEvent()
    {
        if (recording) { SetStatus("Сначала закончите запись"); return; }
        var n = SafeName(InputBox.Ask(this, "Новый проект", "Название (например, Свадьба):"));
        if (n == null) return;
        if (EventNameTaken(n)) { SetStatus("Такой проект уже есть"); return; }
        var from = EventRoot;
        var copy = MessageBox.Show(this,
            $"Скопировать разделы, треки и джинглы из «{cfg.Event ?? MainEvent}»?\nДа — копия как заготовка.\nНет — пустой проект.",
            "Новый проект", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        var dir = System.IO.Path.Combine(EventsDir, n);
        EventBtn.IsEnabled = false; // пока копируется, переключать и удалять проекты нельзя
        try
        {
            Directory.CreateDirectory(dir);
            if (copy)
            {
                SetStatus("Копирую, подождите…");
                await Task.Run(() =>
                {
                    CopyDir(System.IO.Path.Combine(from, "Разделы"), System.IO.Path.Combine(dir, "Разделы"), true);
                    CopyDir(System.IO.Path.Combine(from, "Джинглы"), System.IO.Path.Combine(dir, "Джинглы"), false);
                });
            }
        }
        catch
        {
            try { Directory.Delete(dir, true); } catch { }
            SetStatus("Не получилось создать проект: копия прервана, недокопированное убрано");
            EventBtn.IsEnabled = true;
            return;
        }
        EventBtn.IsEnabled = true;
        SwitchEvent(n);
    }

    // копия папки с сохранением времени создания (от него зависит порядок треков); записи не копируем
    static void CopyDir(string src, string dst, bool skipRecordings)
    {
        if (!Directory.Exists(src)) return;
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src).OrderBy(f => File.GetCreationTimeUtc(f)))
        {
            var to = System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f));
            File.Copy(f, to, true);
            File.SetCreationTimeUtc(to, File.GetCreationTimeUtc(f));
            if (System.IO.Path.GetFileName(f) == OrderFile) File.SetAttributes(to, FileAttributes.Hidden);
        }
        foreach (var d in Directory.GetDirectories(src))
        {
            var nm = System.IO.Path.GetFileName(d);
            var to = System.IO.Path.Combine(dst, nm);
            if (skipRecordings && nm == RecSection) { Directory.CreateDirectory(to); continue; }
            CopyDir(d, to, false);
            Directory.SetCreationTimeUtc(to, Directory.GetCreationTimeUtc(d));
        }
    }

    void RenameEvent()
    {
        if (cfg.Event == null || recording) return;
        var old = cfg.Event;
        var n = SafeName(InputBox.Ask(this, "Переименовать проект", "Новое название:", old));
        if (n == null || n == old) return;
        if (EventNameTaken(n)) { SetStatus("Такой проект уже есть"); return; }
        StopAll();
        try { Directory.Move(System.IO.Path.Combine(EventsDir, old), System.IO.Path.Combine(EventsDir, n)); }
        catch { SetStatus("Не получилось переименовать"); return; }
        if (cfg.LastByEvent.Remove(old, out var ls)) cfg.LastByEvent[n] = ls;
        cfg.Event = n;
        ReloadEvent();
    }

    void DeleteEvent()
    {
        if (cfg.Event == null || recording) return;
        var n = cfg.Event;
        if (MessageBox.Show(this, $"Удалить проект «{n}» со всеми разделами, треками и джинглами?\nФайлы уйдут в корзину, их можно будет вернуть.",
                "Удаление проекта", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        StopAll();
        try { FileSystem.DeleteDirectory(System.IO.Path.Combine(EventsDir, n), UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); }
        catch { SetStatus("Не получилось удалить проект"); return; }
        cfg.LastByEvent.Remove(n);
        cfg.Event = null;
        ReloadEvent();
    }

    // ---------- добавление файлов ----------

    int CopyInto(IEnumerable<string> paths, string dir)
    {
        Directory.CreateDirectory(dir);
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                try { files.AddRange(Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Where(IsAudio)); }
                catch { }
            }
            else if (File.Exists(p) && IsAudio(p)) files.Add(p);
        }
        int n = 0;
        foreach (var f in files)
        {
            try
            {
                if (string.Equals(System.IO.Path.GetDirectoryName(f), dir, StringComparison.OrdinalIgnoreCase)) continue;
                var name = System.IO.Path.GetFileNameWithoutExtension(f);
                var ext = System.IO.Path.GetExtension(f);
                var dest = System.IO.Path.Combine(dir, name + ext);
                if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(f).Length) continue; // такой же файл уже тут
                for (int i = 2; File.Exists(dest); i++) dest = System.IO.Path.Combine(dir, $"{name} ({i}){ext}");
                File.Copy(f, dest);
                n++;
            }
            catch { }
        }
        return n;
    }

    async void AddToSection(IEnumerable<string> paths, string section)
    {
        var list = paths.ToList();
        var dir = SectionDir(section);
        SetStatus("Копирую, подождите…");
        int n;
        try { n = await Task.Run(() => CopyInto(list, dir)); }
        catch { SetStatus("Не получилось добавить файлы"); return; }
        SetStatus(n == 0 ? "Музыкальных файлов не найдено" : $"Добавлено в «{section}»: {Plural(n, "трек", "трека", "треков")}");
        if (n > 0) RefreshAll();
    }

    static string FilterAudio => "Музыка|*.mp3;*.wav;*.flac;*.m4a;*.wma;*.aac;*.aif;*.aiff|Все файлы|*.*";

    void AddTracks_Click(object sender, RoutedEventArgs e)
    {
        if (shownSection == null) return;
        var d = new OpenFileDialog { Multiselect = true, Filter = FilterAudio, Title = $"Добавить в «{shownSection}»" };
        if (d.ShowDialog(this) == true) AddToSection(d.FileNames, shownSection);
    }

    static string[] DroppedFiles(DragEventArgs e) => e.Data.GetData(DataFormats.FileDrop) as string[];

    // ---------- плавная прокрутка колесом ----------

    double wheelTarget = double.NaN;
    bool wheelRunning;

    void TrackList_Wheel(object sender, MouseWheelEventArgs e)
    {
        var sv = FindScroll(TrackList);
        if (sv == null || sv.ScrollableHeight <= 0) return;
        if (double.IsNaN(wheelTarget) || !wheelRunning) wheelTarget = sv.VerticalOffset;
        wheelTarget = Math.Clamp(wheelTarget - e.Delta * 0.9, 0, sv.ScrollableHeight);
        e.Handled = true;
        if (wheelRunning) return;
        wheelRunning = true;
        void Step(object o, EventArgs a)
        {
            wheelTarget = Math.Clamp(wheelTarget, 0, sv.ScrollableHeight);
            double diff = wheelTarget - sv.VerticalOffset;
            if (Math.Abs(diff) < 0.6) { sv.ScrollToVerticalOffset(wheelTarget); CompositionTarget.Rendering -= Step; wheelRunning = false; return; }
            sv.ScrollToVerticalOffset(sv.VerticalOffset + diff * 0.22);
        }
        CompositionTarget.Rendering += Step;
    }

    // ---------- выбранные треки: поднять / опустить / в начало / в конец ----------

    // delta: -1 вверх, +1 вниз на одну позицию; int.MinValue — в начало, int.MaxValue — в конец. Порядок остальных не меняется.
    void MoveSelected(int delta)
    {
        if (SearchBox.Text.Length > 0) { SetStatus("При поиске порядок не меняем: очистите поиск (Esc)"); return; }
        var sel = SelectedSongs();
        if (sel.Count == 0 || shownSection == null) return;
        var set = new HashSet<Song>(sel);
        var order = songs.ToList();
        if (delta == int.MinValue) order = sel.Concat(order.Where(x => !set.Contains(x))).ToList();
        else if (delta == int.MaxValue) order = order.Where(x => !set.Contains(x)).Concat(sel).ToList();
        else if (delta < 0)
        {
            for (int i = 1; i < order.Count; i++)
                if (set.Contains(order[i]) && !set.Contains(order[i - 1])) (order[i - 1], order[i]) = (order[i], order[i - 1]);
        }
        else
        {
            for (int i = order.Count - 2; i >= 0; i--)
                if (set.Contains(order[i]) && !set.Contains(order[i + 1])) (order[i + 1], order[i]) = (order[i], order[i + 1]);
        }
        bool changed = false;
        for (int i = 0; i < order.Count; i++)
        {
            int from = songs.IndexOf(order[i]);
            if (from != i) { songs.Move(from, i); changed = true; }
        }
        if (!changed) return;
        for (int i = 0; i < songs.Count; i++) songs[i].Num = i + 1;
        SaveOrder(shownSection, songs);
        if (playingSection == shownSection) RebuildQueue();
        TrackList.SelectedItems.Clear();
        foreach (var o in sel) TrackList.SelectedItems.Add(o);
        TrackList.ScrollIntoView(sel[0]);
    }


    // ---------- разделы: порядок, листание, папки ----------

    const string SectionFmt = "VertushkaSection";
    Point secDragStart;
    SectionItem secDrag;

    void SectionItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        secDragStart = e.GetPosition(null);
        secDrag = (sender as ListBoxItem)?.DataContext as SectionItem;
    }

    void SectionItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || secDrag == null) return;
        var d = e.GetPosition(null) - secDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var it = secDrag; secDrag = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(SectionFmt, it), DragDropEffects.Move);
    }

    // раздел встаёт на место того, на который его бросили
    void ReorderSection(string moved, string target)
    {
        if (moved == target) return;
        var names = OrderedSectionDirs().Select(System.IO.Path.GetFileName).ToList();
        int from = names.IndexOf(moved), to = names.IndexOf(target);
        if (from < 0 || to < 0) return;
        names.RemoveAt(from);
        names.Insert(to, moved);
        WriteOrder(SectionsDir, names, SectionsOrderFile);
        LoadSections(shownSection);
    }

    void SecScroll(double delta)
    {
        var sv = FindScroll(SectionList);
        if (sv != null) sv.ScrollToHorizontalOffset(Math.Clamp(sv.HorizontalOffset + delta, 0, sv.ScrollableWidth));
    }

    void SecLeft_Click(object sender, RoutedEventArgs e) => SecScroll(-260);
    void SecRight_Click(object sender, RoutedEventArgs e) => SecScroll(260);

    void SectionList_Wheel(object sender, MouseWheelEventArgs e)
    {
        SecScroll(-e.Delta * 0.8);
        e.Handled = true;
    }

    // папка с треками = новый раздел с её названием (подпапки тоже забираются)
    async void AddFolders(IEnumerable<string> folders)
    {
        string last = null; int total = 0, made = 0;
        SetStatus("Копирую папки, подождите…");
        foreach (var f in folders)
        {
            var name = SafeName(System.IO.Path.GetFileName(f.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)));
            if (name == null) continue;
            var dir = SectionDir(name);
            bool existed = Directory.Exists(dir);
            int n;
            try { n = await Task.Run(() => CopyInto(new[] { f }, dir)); }
            catch { continue; }
            if (n > 0) { last = name; total += n; if (!existed) made++; }
            else if (!existed) { try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { } }
        }
        if (last == null) { SetStatus("В папке нет музыкальных файлов"); return; }
        LoadSections(last);
        SetStatus($"Добавлено: разделов {made}, {Plural(total, "трек", "трека", "треков")}");
    }

    // бросили файлы и папки: папки — в разделы, одиночные файлы — в раздел target
    void DropMixed(string[] items, string target)
    {
        var dirs = items.Where(Directory.Exists).ToList();
        var files = items.Where(x => !Directory.Exists(x)).ToList();
        if (dirs.Count > 0) AddFolders(dirs);
        if (files.Count > 0 && target != null) AddToSection(files, target);
    }

    void Strip_Drop(object sender, DragEventArgs e)
    {
        var f = DroppedFiles(e);
        if (f != null) DropMixed(f, shownSection);
        e.Handled = true;
    }

    // ---------- ручной порядок треков (перетаскивание) ----------

    const string SongFmt = "VertushkaSong";
    const string SongsFmt = "VertushkaSongs"; // все перетаскиваемые треки (List<Song>)
    Point dragStart;
    Song dragSong;
    ListBoxItem marked;

    static T Up<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    bool sweeping;
    int sweepAnchor, sweepLast;

    void TrackList_PreviewMove(object sender, MouseEventArgs e)
    {
        if (!sweeping) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndSweep(); return; }
        var p = e.GetPosition(TrackList);
        var sv = FindScroll(TrackList);
        if (sv != null)
        {
            if (p.Y < 24) sv.ScrollToVerticalOffset(sv.VerticalOffset - 16);
            else if (p.Y > TrackList.ActualHeight - 24) sv.ScrollToVerticalOffset(sv.VerticalOffset + 16);
        }
        var it = Up<ListBoxItem>(TrackList.InputHitTest(new Point(Math.Clamp(p.X, 4, Math.Max(5, TrackList.ActualWidth - 4)), Math.Clamp(p.Y, 2, Math.Max(3, TrackList.ActualHeight - 2)))) as DependencyObject);
        if (it == null) return;
        int idx = TrackList.Items.IndexOf(it.DataContext);
        if (idx < 0 || idx == sweepLast) return;
        sweepLast = idx;
        int lo = Math.Min(sweepAnchor, idx), hi = Math.Max(sweepAnchor, idx);
        TrackList.SelectedItems.Clear();
        for (int i = lo; i <= hi; i++) TrackList.SelectedItems.Add(TrackList.Items[i]);
    }

    void TrackList_PreviewUp(object sender, MouseButtonEventArgs e)
    {
        if (!sweeping) return;
        EndSweep();
        e.Handled = true;
    }

    void TrackList_LostCapture(object sender, MouseEventArgs e) { sweeping = false; }

    void EndSweep()
    {
        sweeping = false;
        if (TrackList.IsMouseCaptured) TrackList.ReleaseMouseCapture();
    }

    ListBoxItem pendingSingle; // клик по уже выбранному треку среди нескольких: решаем при отпускании (клик или перетаскивание)

    void Track_MouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(null);
        dragSong = (sender as ListBoxItem)?.DataContext as Song;
        pendingSingle = null;
        var item = sender as ListBoxItem;
        bool mod = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        // зажать левую кнопку на номере трека и вести вниз/вверх — выделяются все треки между
        if (item != null && !mod && e.ClickCount == 1 && e.GetPosition(item).X < 64 && SearchBox.Text.Length == 0)
        {
            sweepAnchor = TrackList.Items.IndexOf(item.DataContext);
            if (sweepAnchor >= 0)
            {
                sweeping = true; sweepLast = sweepAnchor; dragSong = null;
                TrackList.SelectedItems.Clear();
                TrackList.SelectedItems.Add(item.DataContext);
                TrackList.CaptureMouse();
                e.Handled = true;
                return;
            }
        }
        if (item != null && item.IsSelected && TrackList.SelectedItems.Count > 1 && !mod && e.ClickCount == 1)
        {
            pendingSingle = item;
            e.Handled = true; // не сбрасываем выбор: возможно, это начало перетаскивания всех выбранных
        }
    }

    void Track_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (pendingSingle != null && pendingSingle == sender)
        {
            var it = pendingSingle; pendingSingle = null;
            TrackList.SelectedItems.Clear();
            it.IsSelected = true;
        }
    }

    // выбранные треки по порядку в списке
    List<Song> SelectedSongs() => TrackList.SelectedItems.OfType<Song>().OrderBy(s => songs.IndexOf(s)).ToList();

    void Search_Changed(object sender, TextChangedEventArgs e) => songsView?.Refresh();

    void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SearchBox.Text = "";
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    void Track_MouseMove(object sender, MouseEventArgs e)
    {
        if (SearchBox.Text.Length > 0) return; // при поиске порядок не меняем
        if (e.LeftButton != MouseButtonState.Pressed || dragSong == null) return;
        var d = e.GetPosition(null) - dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var song = dragSong;
        dragSong = null; pendingSingle = null;
        var group = TrackList.SelectedItems.Contains(song) ? SelectedSongs() : new List<Song> { song };
        var data = new DataObject(SongFmt, song);
        data.SetData(SongsFmt, group);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
        ClearMark();
    }

    void ClearMark()
    {
        if (marked != null) marked.Tag = null;
        marked = null;
    }

    // куда встанет трек: индекс в списке и сторона отметки
    (int index, ListBoxItem item, string side) DropTarget(DragEventArgs e, Song s)
    {
        var group = e.Data.GetData(SongsFmt) as List<Song>;
        int di = group != null && group.Count > 1 ? group.Min(g => songs.IndexOf(g)) : songs.IndexOf(s);
        var item = Up<ListBoxItem>(TrackList.InputHitTest(e.GetPosition(TrackList)) as DependencyObject);
        int ti;
        if (item != null) ti = songs.IndexOf(item.DataContext as Song);
        else
        {
            ti = songs.Count - 1;
            item = ti >= 0 ? TrackList.ItemContainerGenerator.ContainerFromIndex(ti) as ListBoxItem : null;
        }
        if (di < 0 || ti < 0 || di == ti) return (-1, null, null);
        if (group != null && group.Count > 1 && group.Any(g => songs.IndexOf(g) == ti)) return (-1, null, null); // на самого себя
        return (ti, item, di < ti ? "below" : "above");
    }

    void AutoScroll(DragEventArgs e)
    {
        var sv = FindScroll(TrackList);
        if (sv == null) return;
        double y = e.GetPosition(TrackList).Y;
        if (y < 40) sv.ScrollToVerticalOffset(sv.VerticalOffset - 14);
        else if (y > TrackList.ActualHeight - 40) sv.ScrollToVerticalOffset(sv.VerticalOffset + 14);
    }

    static ScrollViewer FindScroll(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var r = FindScroll(VisualTreeHelper.GetChild(d, i));
            if (r != null) return r;
        }
        return null;
    }

    void TrackList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(SongFmt) && sender == TrackList)
        {
            var song = e.Data.GetData(SongFmt) as Song;
            var t = DropTarget(e, song);
            ClearMark();
            if (t.item != null) { t.item.Tag = t.side; marked = t.item; }
            AutoScroll(e);
            e.Effects = DragDropEffects.Move;
        }
        else e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void TrackList_DragLeave(object sender, DragEventArgs e) => ClearMark();

    void TrackList_Drop(object sender, DragEventArgs e)
    {
        ClearMark();
        if (e.Data.GetData(SongFmt) is Song s)
        {
            var t = DropTarget(e, s);
            if (t.index >= 0)
            {
                if (e.Data.GetData(SongsFmt) is List<Song> g && g.Count > 1) ReorderSongs(g, t.index, t.side == "below");
                else ReorderSong(s, t.index);
            }
        }
        else
        {
            var f = DroppedFiles(e);
            if (f != null) DropMixed(f, shownSection);
        }
        e.Handled = true;
    }

    // несколько треков одним блоком: встают до или после трека с индексом target
    void ReorderSongs(List<Song> moved, int target, bool below)
    {
        var ordered = moved.OrderBy(m => songs.IndexOf(m)).ToList();
        var anchor = songs[target];
        var rest = songs.Where(x => !ordered.Contains(x)).ToList();
        int at = rest.IndexOf(anchor) + (below ? 1 : 0);
        rest.InsertRange(Math.Clamp(at, 0, rest.Count), ordered);
        for (int i = 0; i < rest.Count; i++)
        {
            int from = songs.IndexOf(rest[i]);
            if (from != i) songs.Move(from, i);
        }
        for (int i = 0; i < songs.Count; i++) songs[i].Num = i + 1;
        SaveOrder(shownSection, songs);
        if (playingSection == shownSection) RebuildQueue();
        TrackList.SelectedItems.Clear();
        foreach (var o in ordered) TrackList.SelectedItems.Add(o);
    }

    void ReorderSong(Song s, int to)
    {
        int from = songs.IndexOf(s);
        if (from < 0 || from == to) return;
        songs.Move(from, to);
        for (int i = 0; i < songs.Count; i++) songs[i].Num = i + 1;
        SaveOrder(shownSection, songs);
        if (playingSection == shownSection) RebuildQueue();
    }

    void Section_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is not ListBoxItem i) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || (e.Data.GetData(SongFmt) is Song s && i.DataContext is SectionItem si && si.Name != s.Section)
            || (e.Data.GetData(SectionFmt) is SectionItem ms && i.DataContext is SectionItem ti && ti.Name != ms.Name)) i.Tag = "drop";
    }

    void Section_DragLeave(object sender, DragEventArgs e) { if (sender is ListBoxItem i) i.Tag = null; }

    void Section_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(SongFmt) || e.Data.GetDataPresent(SectionFmt)) e.Effects = DragDropEffects.Move;
        else e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Section_Drop(object sender, DragEventArgs e)
    {
        if (sender is not ListBoxItem i) return;
        i.Tag = null;
        if (e.Data.GetData(SectionFmt) is SectionItem movedSec)
        {
            if (i.DataContext is SectionItem tsec) ReorderSection(movedSec.Name, tsec.Name);
        }
        else if (e.Data.GetData(SongFmt) is Song song)
        {
            var group = e.Data.GetData(SongsFmt) as List<Song> ?? new List<Song> { song };
            if (i.DataContext is SectionItem si && si.Name != song.Section) MoveSongs(group, si.Name);
        }
        else
        {
            var f = DroppedFiles(e);
            if (f != null && i.DataContext is SectionItem s) DropMixed(f, s.Name);
        }
        e.Handled = true;
    }

    // ---------- меню правой кнопкой ----------

    void List_RightClick(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d != null && d is not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        if (d is ListBoxItem item && !item.IsSelected) { TrackList.SelectedItems.Clear(); item.IsSelected = true; }
    }

    void TrackList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (TrackList.SelectedItem is not Song song) { e.Handled = true; return; }
        var m = new ContextMenu();
        if (TrackList.SelectedItems.Count > 1)
        {
            var sel = SelectedSongs(); int n = sel.Count;
            var pn = new MenuItem { Header = $"Играть следующими ({n})" };
            pn.Click += (a, b) => { for (int i = sel.Count - 1; i >= 0; i--) { upNext.RemoveAll(u => u.Path == sel[i].Path); upNext.Insert(0, sel[i]); } SetStatus($"Следующими: {n} треков"); MarkPlaying(); };
            m.Items.Add(pn);
            var ql = new MenuItem { Header = $"В конец очереди ({n})" };
            ql.Click += (a, b) => { foreach (var x in sel) { upNext.RemoveAll(u => u.Path == x.Path); upNext.Add(x); } SetStatus($"В конец очереди: {n} треков"); MarkPlaying(); };
            m.Items.Add(ql);
            var mv = new MenuItem { Header = $"Переместить в раздел ({n})" };
            foreach (var sName in SectionDirs().Select(System.IO.Path.GetFileName).Where(x => x != song.Section))
            {
                var target = sName;
                var it = new MenuItem { Header = target };
                it.Click += (a, b) => MoveSongs(sel, target);
                mv.Items.Add(it);
            }
            m.Items.Add(mv);
            m.Items.Add(new Separator());
            var mu1 = new MenuItem { Header = "Поднять выше (Ctrl+Shift+↑)" }; mu1.Click += (a, b) => MoveSelected(-1); m.Items.Add(mu1);
            var md1 = new MenuItem { Header = "Опустить ниже (Ctrl+Shift+↓)" }; md1.Click += (a, b) => MoveSelected(1); m.Items.Add(md1);
            var mt = new MenuItem { Header = "В начало списка" }; mt.Click += (a, b) => MoveSelected(int.MinValue); m.Items.Add(mt);
            var mb = new MenuItem { Header = "В конец списка" }; mb.Click += (a, b) => MoveSelected(int.MaxValue); m.Items.Add(mb);
            m.Items.Add(new Separator());
            var dl = new MenuItem { Header = $"Убрать из раздела ({n}, в корзину)" };
            dl.Click += (a, b) => DeleteSongs(sel);
            m.Items.Add(dl);
            TrackList.ContextMenu = m;
            return;
        }
        var play = new MenuItem { Header = "Играть" };
        play.Click += (a, b) => PlayFromList(song);
        m.Items.Add(play);
        var pre = new MenuItem { Header = "Послушать в наушниках" };
        pre.Click += (a, b) => StartPreview(song);
        m.Items.Add(pre);
        var nxt = new MenuItem { Header = "Играть следующим" };
        nxt.Click += (a, b) => PlayNextNow(song);
        m.Items.Add(nxt);
        var last = new MenuItem { Header = "В конец очереди" };
        last.Click += (a, b) => QueueLast(song);
        m.Items.Add(last);
        if (song.HasNext)
        {
            var un = new MenuItem { Header = "Убрать из «Далее»" };
            un.Click += (a, b) => RemoveFromNext(song.Path);
            m.Items.Add(un);
        }
        var move =new MenuItem { Header = "Переместить в раздел" };
        foreach (var s in SectionDirs().Select(System.IO.Path.GetFileName).Where(n => n != song.Section))
        {
            var target = s;
            var it = new MenuItem { Header = target };
            it.Click += (a, b) => MoveSong(song, target);
            move.Items.Add(it);
        }
        m.Items.Add(move);
        var up1 = new MenuItem { Header = "Поднять выше (Ctrl+Shift+↑)" }; up1.Click += (a, b) => MoveSelected(-1); m.Items.Add(up1);
        var dn1 = new MenuItem { Header = "Опустить ниже (Ctrl+Shift+↓)" }; dn1.Click += (a, b) => MoveSelected(1); m.Items.Add(dn1);
        var top = new MenuItem { Header = "В начало списка" }; top.Click += (a, b) => MoveSelected(int.MinValue); m.Items.Add(top);
        var bot = new MenuItem { Header = "В конец списка" }; bot.Click += (a, b) => MoveSelected(int.MaxValue); m.Items.Add(bot);
        var trim = new MenuItem { Header = "Обрезка (метки на волне)…" };
        trim.Click += (a, b) => EnterTrimFor(song);
        m.Items.Add(trim);
        if (Trims.Has(song.Path))
        {
            var sv = new MenuItem { Header = "Сохранить обрезанный трек файлом…" };
            sv.Click += (a, b) => SaveTrimmed(song);
            m.Items.Add(sv);
            var rs = new MenuItem { Header = "Сбросить обрезку" };
            rs.Click += (a, b) => ResetTrim(song);
            m.Items.Add(rs);
        }
        var show = new MenuItem { Header = "Показать в папке" };
        show.Click += (a, b) => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{song.Path}\"");
        m.Items.Add(show);
        var del = new MenuItem { Header = "Убрать из раздела (в корзину)" };
        del.Click += (a, b) => DeleteSong(song);
        m.Items.Add(del);
        TrackList.ContextMenu = m;
    }

    string[] SectionDirs()
    {
        try { return Directory.Exists(SectionsDir) ? Directory.GetDirectories(SectionsDir) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    // несколько треков сразу в другой раздел
    void MoveSongs(List<Song> list, string target)
    {
        if (list.Count == 1) { MoveSong(list[0], target); return; }
        int ok = 0;
        foreach (var s in list) if (MoveSong(s, target, false)) ok++;
        RefreshAll();
        SetStatus(ok == list.Count ? $"Перемещено треков: {ok} → «{target}»" : $"Перемещено {ok} из {list.Count} (играющий трек или занятый файл не двигается)");
    }

    bool MoveSong(Song s, string target, bool refresh = true)
    {
        try
        {
            var dest = System.IO.Path.Combine(SectionDir(target), System.IO.Path.GetFileName(s.Path));
            var name = System.IO.Path.GetFileNameWithoutExtension(dest); var ext = System.IO.Path.GetExtension(dest);
            for (int i = 2; File.Exists(dest); i++) dest = System.IO.Path.Combine(SectionDir(target), $"{name} ({i}){ext}");
            var moved = Cues.Take(s.Path); // метки привязаны к пути: едут вместе с треком
            var movedTrim = Trims.Take(s.Path);
            try { File.Move(s.Path, dest); }
            catch { Cues.Save(s.Path, moved); Trims.Save(s.Path, movedTrim[0], movedTrim[1]); throw; }
            bool dataSaved = Cues.Save(dest, moved) & Trims.Save(dest, movedTrim[0], movedTrim[1]);
            RemoveFromNext(s.Path);
            File.SetCreationTimeUtc(dest, DateTime.UtcNow); // чтобы встал в конец раздела
            ForgetInOrder(s.Section, System.IO.Path.GetFileName(s.Path));
            ForgetInOrder(target, System.IO.Path.GetFileName(dest));
            if (refresh) { SetStatus($"«{s.Name}» перемещён в «{target}»" + (dataSaved ? "" : " (метки и обрезка не сохранились: файл данных занят)")); RefreshAll(); }
            return true;
        }
        catch (Exception ex)
        {
            if (refresh) SetStatus(cur != null && cur.Song.Path == s.Path ? "Не получилось: этот трек сейчас играет. Включите другой и повторите" : "Не получилось переместить: " + ex.Message);
            return false;
        }
    }

    static bool IsLocked(string path)
    {
        try { using var f = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch { return false; }
    }

    // убрать файл в корзину вместе с метками и обрезкой; играющий или занятый файл не трогаем (и без системного окна «файл занят»)
    bool TryRecycle(Song s)
    {
        if (cur != null && cur.Song.Path == s.Path) return false;
        if (IsLocked(s.Path)) return false;
        var goneCues = Cues.Take(s.Path);
        var goneTrim = Trims.Take(s.Path);
        try { FileSystem.DeleteFile(s.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); }
        catch { Cues.Save(s.Path, goneCues); Trims.Save(s.Path, goneTrim[0], goneTrim[1]); return false; }
        RemoveFromNext(s.Path);
        ForgetInOrder(s.Section, System.IO.Path.GetFileName(s.Path));
        return true;
    }

    void DeleteSongs(List<Song> list)
    {
        if (MessageBox.Show(this, $"Убрать из раздела треки: {list.Count}?\nФайлы уйдут в корзину.", "Удаление треков", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        int ok = 0;
        foreach (var s in list) if (TryRecycle(s)) ok++;
        RefreshAll();
        SetStatus(ok == list.Count ? $"Убрано треков: {ok}" : $"Убрано {ok} из {list.Count} (играющий или занятый файл не убирается)");
    }

    void DeleteSong(Song s)
    {
        if (MessageBox.Show(this, $"Убрать «{s.Name}» из раздела?\nФайл уйдёт в корзину.", "Удаление трека", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (TryRecycle(s)) RefreshAll();
        else SetStatus("Не получилось: файл играет или занят другой программой");
    }

    // ---------- плеер ----------

    // длительность затухания в конце трека; 0 — выключено
    int Crossfade => cfg.FadeEnabled ? (int)FadeSlider.Value : 0;

    string FadeLabel() => cfg.FadeEnabled ? $"{(int)FadeSlider.Value} с" : "выкл";

    // короткий трек затухает не дольше 40% своей длины, чтобы не «съедать» себя целиком
    double FadeFor(Slot s) => Math.Min(Crossfade, (EffEnd(s) - EffStart(s)) * 0.4);

    // ---------- «Далее» ----------

    bool HasNextTrack => upNext.Count > 0 || qi + 1 < queue.Count;

    void PlayNextNow(Song s)
    {
        upNext.RemoveAll(u => u.Path == s.Path);
        upNext.Insert(0, s);
        SetStatus($"Следующим: «{s.Name}»");
        MarkPlaying();
    }

    void QueueLast(Song s)
    {
        upNext.RemoveAll(u => u.Path == s.Path);
        upNext.Add(s);
        SetStatus($"В конец очереди: «{s.Name}»");
        MarkPlaying();
    }

    void RemoveFromNext(string path)
    {
        if (upNext.RemoveAll(u => u.Path == path) > 0) MarkPlaying();
    }

    void ClearNext()
    {
        upNext.Clear();
        MarkPlaying();
    }

    void NextClear_Click(object sender, RoutedEventArgs e) => ClearNext();

    // строка над плеером и панель справа: что заиграет следующим
    string nextSig = "\u0001";

    void UpdateNextText()
    {
        string t = "", name = null;
        if (upNext.Count > 0) { name = upNext[0].Name; t = "Далее: " + name + (upNext.Count > 1 ? $"   ещё {upNext.Count - 1}" : ""); }
        else if (cur != null) t = qi + 1 < queue.Count ? "Следующий: " + queue[qi + 1].Name : "Дальше ничего нет";
        if (NextText.Text != t) NextText.Text = t;
        var vis = upNext.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (NextClearBtn.Visibility != vis) NextClearBtn.Visibility = vis;

        // правая панель: сначала «Далее», если пусто — ближайшие по порядку
        var src = upNext.Count > 0 ? upNext.Take(300).ToList() : (cur != null ? queue.Skip(qi + 1).Take(300).ToList() : new List<Song>());
        var sig = (upNext.Count > 0 ? "u" : "q") + string.Join("|", src.Select(x => x.Path));
        if (sig == nextSig) return;
        nextSig = sig;
        NextItems.ItemsSource = src.Select((x, i) => new { N = (i + 1).ToString(), x.Name }).ToList();
        NextHead.Text = upNext.Count > 0 ? "ДАЛЕЕ" : "ДАЛЬШЕ ПО СПИСКУ";
        NextEmpty.Visibility = src.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetFadeEnabled(bool on)
    {
        cfg.FadeEnabled = on;
        FadeTgl.IsChecked = on;
        FadeSlider.IsEnabled = on;
        FadeText.Text = FadeLabel();
    }

    void FadeTgl_Click(object sender, RoutedEventArgs e) => SetFadeEnabled(FadeTgl.IsChecked == true);

    void Track_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem i && i.DataContext is Song s) PlayFromList(s);
    }

    void PlayFromList(Song s)
    {
        queue = songs.ToList();
        qi = queue.IndexOf(s);
        playingSection = shownSection;
        Start(queue[qi], true);
    }

    bool Start(Song s, bool cross, bool viaNext = false)
    {
        Slot ns = null;
        try
        {
            ns = new Slot(s) { FromNext = viaNext };
            var tr = Trims.Get(s.Path);
            ns.TrimStart = tr[0]; ns.TrimEnd = tr[1];
            if (ns.TrimStart > 0.05) ns.SeekClean(ns.TrimStart); // играет от метки «начало», без щелчка
            var old = cur;
            bool doFade = cross && old != null && Crossfade > 0 && old.Out.PlaybackState == PlaybackState.Playing;
            // старый трек уходит за оставшееся время, но не дольше выбранного затухания
            double dur = doFade ? Math.Max(0.5, Math.Min(Crossfade, Remain(old))) : 0;
            if (doFade) { ns.Factor = 0f; ns.FadeTo(1f, dur); }
            // громкость и плавное нарастание выставляем ДО первого звука: иначе первые ~100 мс трек играет на полной громкости (у обрезанного — с самого громкого места)
            ns.Tick((float)cfg.Volume * (jinglesActive > 0 ? 0.35f : 1f) * duck);
            ns.Out.Play();
            cur = ns;
            if (old != null)
            {
                if (doFade)
                {
                    fading.Add(old);
                    old.OnZero = () => { fading.Remove(old); old.Dispose(); };
                    old.FadeTo(0f, dur);
                }
                else old.Dispose();
            }
        }
        catch (Exception ex)
        {
            App.LogCrash("Start", ex);
            if (ns != cur) ns?.Dispose();
            SetStatus($"Не удалось включить «{s.Name}». Проверьте файл и звуковое устройство");
            return false;
        }
        SetLoopOn(false);
        cueSec = Cues.Get(s.Path);
        Wave.Cues = cueSec;
        trimEdit = false;
        ShowTrim();
        if (history.Count == 0 || history[^1].Path != s.Path) { history.Add(s); if (history.Count > 15) history.RemoveAt(0); }
        MarkPlaying();
        LoadWave(s.Path);
        return true;
    }

    async void LoadWave(string path)
    {
        Wave.Peaks = null;
        Wave.Progress = 0;
        Wave.Loading = true;
        var peaks = await WaveformCache.GetAsync(path);
        if (cur?.Song.Path == path) { Wave.Peaks = peaks; Wave.Loading = false; }
    }

    void Next(bool cross)
    {
        if (upNext.Count > 0)
        {
            // «Далее» идёт поверх очереди: место в очереди (qi) не меняется, после него очередь продолжится
            var n = upNext[0];
            upNext.RemoveAt(0);
            if (!Start(n, cross, true) && !cross) StopAll();
            return;
        }
        if (qi + 1 >= queue.Count) { if (!cross) StopAll(); return; }
        qi++;
        // не получилось включить следующий при остановленном треке — останавливаемся, а не перебираем очередь
        if (!Start(queue[qi], cross) && !cross) StopAll();
    }

    void Prev()
    {
        if (cur != null && (cur.FromNext || cur.Reader.CurrentTime.TotalSeconds - EffStart(cur) > 3)) { cur.SeekClean(EffStart(cur)); return; }
        if (qi > 0) { qi--; Start(queue[qi], true); }
    }

    bool repeatOn;

    void Repeat_Click(object sender, RoutedEventArgs e) => SetRepeat(RepeatBtn.IsChecked == true);

    void SetRepeat(bool on)
    {
        repeatOn = on;
        RepeatBtn.IsChecked = on;
        SetStatus(on ? "Повтор трека включён: играет по кругу" : "Повтор трека выключен");
    }

    // конец трека: при включённом повторе он же заново, иначе следующий
    void NextOrRepeat(bool cross)
    {
        if (repeatOn && cur != null && cur.Reader.TotalTime.TotalSeconds >= 1.0) { if (!Start(cur.Song, cross) && !cross) StopAll(); return; }
        Next(cross);
    }

    bool HasNextOrRepeat => repeatOn || HasNextTrack;

    void PlayPause()
    {
        if (cur == null)
        {
            var s = TrackList.SelectedItem as Song ?? songs.FirstOrDefault();
            if (s != null) PlayFromList(s);
            return;
        }
        if (cur.Out.PlaybackState == PlaybackState.Playing)
        {
            cur.OnZero = null; cur.Target = cur.Factor;
            cur.Out.Pause();
        }
        else if (cur.Out.PlaybackState == PlaybackState.Paused)
        {
            cur.OnZero = null; cur.Factor = 1f; cur.Target = 1f;
            cur.Out.Play();
        }
    }

    void FadePause()
    {
        if (cur == null) return;
        var s = cur;
        if (s.Out.PlaybackState == PlaybackState.Playing)
        {
            s.OnZero = () => s.Out.Pause();
            s.FadeTo(0f, Math.Max(Crossfade, 2));
            SetStatus("Затухание…");
        }
        else if (s.Out.PlaybackState == PlaybackState.Paused)
        {
            s.OnZero = null; s.Factor = 0f;
            s.FadeTo(1f, 1);
            s.Out.Play();
        }
    }

    void StopAll()
    {
        cur?.Dispose();
        cur = null;
        foreach (var f in fading) f.Dispose();
        fading.Clear();
        MarkPlaying();
        Wave.Peaks = null;
        Wave.Loading = false;
        Wave.Progress = 0; Wave.Duration = 0; Wave.FadeSeconds = 0; Wave.SetLoop(-1, -1);
        SetLoopOn(false);
        cueSec = new[] { -1.0, -1, -1, -1 };
        Wave.Cues = null;
        trimEdit = false;
        ShowTrim();
        NowCover.ClearValue(Border.BackgroundProperty);
        NowCover.SetResourceReference(Border.BackgroundProperty, "Panel2");
        TimeText.Text = "0:00 / 0:00";
        RemainText.Text = "0:00";
        NowText.Text = "Ничего не играет";
        SyncMini();
        PlayBtn.Content = "";
    }

    static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    void Tick()
    {
        if (++saveTick >= 600) { saveTick = 0; cfg.Save(); } // раз в 30 секунд, чтобы при сбое не потерять настройки
        // «поклон»: музыка плавно уходит до четверти и так же возвращается
        float duckTarget = ducking ? 0.22f : 1f;
        if (duck != duckTarget) duck = duck < duckTarget ? Math.Min(duckTarget, duck + 0.08f) : Math.Max(duckTarget, duck - 0.08f);
        float m = (float)cfg.Volume * (jinglesActive > 0 ? 0.35f : 1f) * duck;
        if (preview != null && preview.Active)
        {
            if (preview.Ended)
            {
                var perr = preview.Error;
                StopPreview();
                if (perr != null) SetStatus("Наушники остановились: устройство отключено или недоступно");
            }
            else PreviewBtn.Content = $"Стоп наушники · {Fmt(preview.Position)}";
        }
        cur?.Tick(m);
        foreach (var f in fading.ToList()) f.Tick(m);

        var c = cur;
        if (c != null)
        {
            var r = c.Reader;
            var st = c.Out.PlaybackState;
            NowText.Text = c.Song.Name;
            var shown = songs.FirstOrDefault(s => s.IsPlaying);
            if (shown != null)
            {
                shown.Progress = r.CurrentTime.TotalSeconds / Math.Max(1, r.TotalTime.TotalSeconds);
                shown.Paused = st == PlaybackState.Paused;
            }
            if (loopOn && !seeking && r.CurrentTime.TotalSeconds >= loopStart + cfg.LoopLen) c.SeekClean(loopStart);
            double totSec = Math.Max(1, r.TotalTime.TotalSeconds);
            Wave.SetLoop(loopOn ? loopStart / totSec : -1, loopOn ? Math.Min(1, (loopStart + cfg.LoopLen) / totSec) : -1);
            Wave.Duration = r.TotalTime.TotalSeconds;
            NowCover.Background = c.Song.CoverBrush;
            double eStart = EffStart(c), eEnd = EffEnd(c), ePos = r.CurrentTime.TotalSeconds;
            bool cut = !trimEdit && (c.TrimStart > 0 || c.TrimEnd > 0);
            Wave.FadeSeconds = cfg.FadeEnabled && !(!trimEdit && c.TrimEnd > 0) ? Math.Min(Crossfade, r.TotalTime.TotalSeconds * 0.4) : 0;
            if (!seeking) Wave.Progress = r.CurrentTime.TotalSeconds / Math.Max(1, r.TotalTime.TotalSeconds);
            if (trimEdit) UpdateZoom(ePos);
            TimeText.Text = cut ? $"{Fmt(TimeSpan.FromSeconds(Math.Max(0, ePos - eStart)))} / {Fmt(TimeSpan.FromSeconds(eEnd - eStart))}" : $"{Fmt(r.CurrentTime)} / {Fmt(r.TotalTime)}";
            var left = eEnd > ePos ? TimeSpan.FromSeconds(eEnd - ePos) : TimeSpan.Zero;
            RemainText.Text = "−" + Fmt(left);
            // последние 30 секунд — цифры акцентным цветом, чтобы заметить издалека
            RemainText.Foreground = left.TotalSeconds < 30 && (eEnd - eStart) > 40 ? (Brush)FindResource("Accent") : (Brush)FindResource("Text");
            var icon = st == PlaybackState.Playing ? "" : "";
            if ((string)PlayBtn.Content != icon) PlayBtn.Content = icon;

            if (st == PlaybackState.Playing && Crossfade > 0 && !c.NextTriggered && !seeking
                && left.TotalSeconds <= FadeFor(c) + 0.05)
            {
                c.NextTriggered = true;
                if (HasNextOrRepeat) NextOrRepeat(true);
                else
                {
                    // последний трек: затихает до тишины и останавливается
                    c.OnZero = () => { if (cur == c) StopAll(); };
                    c.FadeTo(0f, Math.Max(0.3, left.TotalSeconds));
                }
            }
            else if (st == PlaybackState.Playing && !trimEdit && c.TrimEnd > 0 && !c.NextTriggered && ePos >= eEnd - 0.03)
            {
                // дошли до метки «конец» (затухание выключено): трек заканчивается здесь
                c.NextTriggered = true;
                if (HasNextOrRepeat) NextOrRepeat(false); else StopAll();
            }
            else if (c.Ended)
            {
                // отключили устройство — стоим и говорим об этом; иначе трек закончился (даже если файл короче, чем написано в заголовке) — идём дальше
                if (c.StopError != null) { App.LogCrash("Stop", c.StopError); StopAll(); SetStatus("Звук остановился: проверьте наушники и устройство вывода"); }
                else if (HasNextOrRepeat) NextOrRepeat(false);
                else StopAll();
            }
        }
        UpdateNextText();

        SyncMini();
        if (recording)
        {
            bool on = (int)(recWatch.Elapsed.TotalSeconds * 2) % 2 == 0;
            RecBtn.Content = $"{(on ? "●" : "○")}  Стоп  {Fmt(recWatch.Elapsed)}";
        }
        if (statusText.Length > 0 && (DateTime.Now - statusAt).TotalSeconds > 6) { statusText = ""; StatusText.Text = ""; }
    }

    void SetStatus(string s)
    {
        statusText = s; statusAt = DateTime.Now; StatusText.Text = s;
    }

    void Play_Click(object sender, RoutedEventArgs e) => PlayPause();
    void Next_Click(object sender, RoutedEventArgs e) => Next(true);
    void Prev_Click(object sender, RoutedEventArgs e) => Prev();
    void Stop_Click(object sender, RoutedEventArgs e) => StopAll();
    void FadePause_Click(object sender, RoutedEventArgs e) => FadePause();

    void Wave_Scrub(double frac, bool done)
    {
        if (cur == null) { Wave.Progress = 0; seeking = false; return; }
        seeking = !done;
        if (done)
        {
            // вне режима обрезки перематывать можно только внутри «начало … конец»
            double lo = EffStart(cur), hi = Math.Max(lo, EffEnd(cur) - 0.1);
            cur.SeekClean(Math.Max(lo, Math.Min(frac * cur.Reader.TotalTime.TotalSeconds, hi)));
            // перемотали за пределы петли — петля выключается (иначе она тут же вернёт назад)
            if (loopOn && (cur.Reader.CurrentTime.TotalSeconds < loopStart - 0.3 || cur.Reader.CurrentTime.TotalSeconds > loopStart + cfg.LoopLen + 0.3))
            { SetLoopOn(false); SetStatus("Петля выключена"); }
            // перемотали назад из зоны затухания последнего трека — звук возвращается, затухание сработает снова позже
            if (cur.NextTriggered && cur.OnZero != null && Remain(cur) > FadeFor(cur) + 0.5)
            {
                cur.OnZero = null; cur.Factor = 1f; cur.Target = 1f; cur.NextTriggered = false;
            }
        }
    }

    void JingleVol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        cfg.JingleVolume = JingleVol.Value;
    }

    void Vol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        cfg.Volume = VolSlider.Value;
    }

    void Fade_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        cfg.Crossfade = (int)FadeSlider.Value;
        FadeText.Text = FadeLabel();
    }

    void Top_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopTgl.IsChecked == true;
        cfg.Topmost = Topmost;
    }

    // ---------- джинглы ----------

    List<string> JingleFiles() =>
        Directory.EnumerateFiles(JinglesDir).Where(IsAudio).OrderBy(f => File.GetCreationTimeUtc(f)).ThenBy(f => f).ToList();

    void RebuildJingles()
    {
        JinglePanel.Children.Clear();
        var files = JingleFiles();
        for (int i = 0; i < files.Count; i++)
        {
            var path = files[i];
            var full = System.IO.Path.GetFileNameWithoutExtension(path);
            var nm = full;
            var face = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var tNum = new TextBlock { Text = i < 9 ? (i + 1).ToString() : "♪", FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            tNum.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            var tNm = new TextBlock { Text = nm, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 92, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            tNm.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            face.Children.Add(tNum);
            face.Children.Add(tNm);
            var b = new Button
            {
                Content = face, Style = (Style)FindResource("Tile"),
                Margin = new Thickness(0, 3, 8, 3), ToolTip = $"{full}\nПравая кнопка — убрать"
            };
            b.Click += (s, e) => PlayJingle(path);
            var menu = new ContextMenu();
            var rm = new MenuItem { Header = "Убрать джингл" };
            rm.Click += (s, e) =>
            {
                StopJingle(); // файл может играть сейчас
                try { FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); } catch { }
                RebuildJingles();
            };
            menu.Items.Add(rm);
            b.ContextMenu = menu;
            JinglePanel.Children.Add(b);
        }
        if (files.Count == 0)
        {
            var hint = new TextBlock { Text = "Добавьте короткие звуки: гонг, аплодисменты, фанфары", FontSize = 15 };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            JinglePanel.Children.Add(hint);
        }
    }

    // Один джингл за раз: новый запуск сначала гасит прежний (иначе звуки накладываются и не выключаются)
    WaveOutEvent jOut;
    AudioFileReader jRd;
    int jLive; // сколько джинглов сейчас живо (должно быть 0 или 1)

    void JLog(string what)
    {
        if (Environment.GetEnvironmentVariable("VERTUSHKA_JLOG") != "1") return;
        try { File.AppendAllText(System.IO.Path.Combine(Paths.AppData, "jingles.log"), $"{DateTime.Now:HH:mm:ss.fff} {what} live={jLive}\n"); } catch { }
    }

    void StopJingle()
    {
        var o = jOut; var r = jRd;
        jOut = null; jRd = null;
        jinglesActive = 0;
        if (o == null) return;
        try { o.Stop(); } catch { }
        try { o.Dispose(); } catch { }
        try { r?.Dispose(); } catch { }
        jLive--;
        JLog("stop");
    }

    void PlayJingle(string path)
    {
        StopJingle();
        AudioFileReader r = null;
        WaveOutEvent o = null;
        try
        {
            r = new AudioFileReader(path) { Volume = (float)cfg.JingleVolume };
            o = new WaveOutEvent();
            o.Init(r);
            var ro = o;
            // естественный конец (в потоке окна): гасим только если это всё ещё текущий джингл
            o.PlaybackStopped += (s, e) => { if (ReferenceEquals(ro, jOut)) StopJingle(); };
            jOut = o; jRd = r;
            jLive++;
            jinglesActive = 1;
            o.Play();
            JLog("play " + System.IO.Path.GetFileName(path));
        }
        catch
        {
            if (o != null && ReferenceEquals(o, jOut)) StopJingle();
            else { o?.Dispose(); r?.Dispose(); }
            SetStatus("Не удалось включить джингл");
        }
    }

    void StopJingle_Click(object sender, RoutedEventArgs e) => StopJingle();

    void AddJingle_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Multiselect = true, Filter = FilterAudio, Title = "Добавить джинглы" };
        if (d.ShowDialog(this) == true) { CopyInto(d.FileNames, JinglesDir); RebuildJingles(); }
    }

    void Jingle_Drop(object sender, DragEventArgs e)
    {
        var f = DroppedFiles(e);
        if (f != null) { int n = CopyInto(f, JinglesDir); RebuildJingles(); SetStatus($"Добавлено джинглов: {n}"); }
        e.Handled = true;
    }

    // ---------- запись ----------

    // номер выбранного микрофона по имени: номера сдвигаются при подключении и отключении устройств.
    // -2 — микрофонов нет совсем, -1 — не выбран или прежний не найден: берём микрофон по умолчанию из настроек Windows
    int MicIndex()
    {
        int n = WaveInEvent.DeviceCount;
        if (n == 0) return -2;
        if (!string.IsNullOrEmpty(cfg.MicName))
            for (int i = 0; i < n; i++)
                if (WaveInEvent.GetCapabilities(i).ProductName == cfg.MicName) return i;
        return -1;
    }

    string MicName()
    {
        int i = MicIndex();
        return i >= 0 ? WaveInEvent.GetCapabilities(i).ProductName : "по умолчанию (Windows)";
    }

    void UpdateMicLabel()
    {
        int i = MicIndex();
        if (i == -2) { MicBtn.Content = "Микрофон не найден"; return; }
        MicBtn.Content = "Микрофон: " + Short(MicName());
    }

    static string Short(string s) => s.Length > 22 ? s.Substring(0, 21) + "…" : s;

    void Mic_Click(object sender, RoutedEventArgs e)
    {
        if (recording) return;
        UpdateMicLabel(); // список устройств мог измениться с запуска программы
        var m = new ContextMenu();
        if (WaveInEvent.DeviceCount > 0)
        {
            var def = new MenuItem { Header = "По умолчанию (как в Windows)", IsCheckable = true, IsChecked = MicIndex() < 0 };
            def.Click += (a, b) => { cfg.MicName = null; cfg.Save(); UpdateMicLabel(); };
            m.Items.Add(def);
        }
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            string name = WaveInEvent.GetCapabilities(i).ProductName;
            var it = new MenuItem { Header = name, IsCheckable = true, IsChecked = i == MicIndex() };
            it.Click += (a, b) => { cfg.MicName = name; cfg.Save(); UpdateMicLabel(); };
            m.Items.Add(it);
        }
        if (m.Items.Count == 0) { SetStatus("Микрофон не найден. Подключите его и нажмите кнопку ещё раз"); return; }
        m.PlacementTarget = MicBtn;
        m.Placement = PlacementMode.Top;
        m.IsOpen = true;
    }

    void MusicTgl_Click(object sender, RoutedEventArgs e) => cfg.RecMusic = MusicTgl.IsChecked == true;

    void Rec_Click(object sender, RoutedEventArgs e) => ToggleRecord();

    void ToggleRecord()
    {
        if (recording) StopRecording(); else StartRecording();
    }

    // ---------- микрофон: WASAPI (блютуз-гарнитура не «икает» остальным звуком, как со старым WaveIn) ----------

    int recMicDev = -1;
    WaveFormat micSrcFmt;
    Action<byte[], int> micSink;

    // устройство записи Windows по номеру из списка WaveIn (имя там обрезано до 31 знака); -1 — по умолчанию
    MMDevice MicEndpoint(int idx)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            if (idx >= 0)
            {
                string pn = WaveInEvent.GetCapabilities(idx).ProductName;
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    if (pn.Length > 0 && d.FriendlyName.Length > 0 && (d.FriendlyName.StartsWith(pn, StringComparison.OrdinalIgnoreCase) || pn.StartsWith(d.FriendlyName, StringComparison.OrdinalIgnoreCase))) return d;
                return null; // выбранный микрофон не нашли среди устройств WASAPI: пишем через WaveIn именно с него, а не с «по умолчанию»
            }
            return en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }
        catch { return null; }
    }

    // создаёт (не запускает) устройство записи: сначала WASAPI, не вышло — прежний WaveIn
    void OpenMic(int micDev)
    {
        recMicDev = micDev;
        try
        {
            var d = MicEndpoint(micDev);
            if (d != null) { micIn = new WasapiCapture(d, false, 100); WireMic(); return; }
        }
        catch { micIn = null; }
        micIn = new WaveInEvent { DeviceNumber = micDev, WaveFormat = new WaveFormat(44100, 16, 1) };
        WireMic();
    }

    void WireMic()
    {
        var m = micIn;
        micSrcFmt = m.WaveFormat;
        m.DataAvailable += (s, a) =>
        {
            var cv = ToMono16(a.Buffer, a.BytesRecorded, micSrcFmt);
            if (cv != null) micSink?.Invoke(cv, cv.Length);
        };
        m.RecordingStopped += Rec_DeviceStopped;
    }

    // запуск; WASAPI не стартовал — переходим на WaveIn с тем же числом отсчётов в секунду
    void StartMic(Action<byte[], int> sink)
    {
        micSink = sink;
        try { micIn.StartRecording(); return; }
        catch when (micIn is WasapiCapture)
        {
            int rate = micIn.WaveFormat.SampleRate;
            try { micIn.Dispose(); } catch { }
            micIn = new WaveInEvent { DeviceNumber = recMicDev, WaveFormat = new WaveFormat(rate, 16, 1) };
            WireMic();
            micIn.StartRecording();
        }
    }

    // любой формат микрофона → 16 бит, моно, та же частота
    static byte[] ToMono16(byte[] buf, int n, WaveFormat f)
    {
        if (f is WaveFormatExtensible ext) f = ext.ToStandardWaveFormat();
        int ch = Math.Max(1, f.Channels), bytes = Math.Max(1, f.BitsPerSample / 8), frame = ch * bytes;
        int frames = n / frame;
        var o = new byte[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < ch; c++)
            {
                int p = i * frame + c * bytes;
                float v;
                if (f.Encoding == WaveFormatEncoding.IeeeFloat && bytes == 4) v = BitConverter.ToSingle(buf, p);
                else if (f.Encoding == WaveFormatEncoding.Pcm && bytes == 2) v = BitConverter.ToInt16(buf, p) / 32768f;
                else if (f.Encoding == WaveFormatEncoding.Pcm && bytes == 3) v = ((buf[p + 2] << 24) | (buf[p + 1] << 16) | (buf[p] << 8)) / 2147483648f;
                else if (f.Encoding == WaveFormatEncoding.Pcm && bytes == 4) v = BitConverter.ToInt32(buf, p) / 2147483648f;
                else return null;
                sum += v;
            }
            float mv = sum / ch;
            if (float.IsNaN(mv)) mv = 0f;
            short s = (short)Math.Clamp(mv * 32767f, -32768f, 32767f);
            o[i * 2] = (byte)(s & 0xFF); o[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return o;
    }

    void StartRecording()
    {
        if (recording) return;
        UpdateMicLabel(); // микрофон могли подключить после запуска программы
        int micDev = MicIndex();
        if (micDev == -2) { SetStatus("Микрофон не найден: подключите его и нажмите «Запись» ещё раз"); return; }
        bool withMusic = cfg.RecMusic;
        var recBase = $"Запись {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        recPath = System.IO.Path.Combine(SectionDir(RecSection), recBase + ".wav");
        for (int k = 2; File.Exists(recPath); k++) recPath = System.IO.Path.Combine(SectionDir(RecSection), $"{recBase} ({k}).wav");
        try
        {
            if (!withMusic)
            {
                OpenMic(micDev);
                writer = new WaveFileWriter(recPath, new WaveFormat(micIn.WaveFormat.SampleRate, 16, 1));
                StartMic((b, n) => { lock (recLock) { try { writer?.Write(b, 0, n); } catch { } } });
            }
            else
            {
                loop = new WasapiLoopbackCapture();
                var lf = loop.WaveFormat;
                if (lf.Channels > 2) throw new InvalidOperationException("surround");
                OpenMic(micDev);
                int micRate = micIn.WaveFormat.SampleRate;
                loopBuf = new BufferedWaveProvider(lf) { DiscardOnBufferOverflow = true, ReadFully = true, BufferDuration = TimeSpan.FromSeconds(5) };
                micBuf = new BufferedWaveProvider(new WaveFormat(micRate, 16, 1)) { DiscardOnBufferOverflow = true, ReadFully = true, BufferDuration = TimeSpan.FromSeconds(5) };
                ISampleProvider music = loopBuf.ToSampleProvider();
                if (lf.Channels == 1) music = new MonoToStereoSampleProvider(music);
                ISampleProvider voiceMono = micBuf.ToSampleProvider();
                if (micRate != lf.SampleRate) voiceMono = new WdlResamplingSampleProvider(voiceMono, lf.SampleRate);
                ISampleProvider voice = new MonoToStereoSampleProvider(voiceMono);
                mixer = new MixingSampleProvider(new[] { music, voice }) { ReadFully = true };
                writer = new WaveFileWriter(recPath, new WaveFormat(lf.SampleRate, 16, 2));
                var lb = loopBuf; var mb = micBuf; // свои ссылки: поля обнуляются при остановке записи
                loop.DataAvailable += (s, a) => { try { lb.AddSamples(a.Buffer, 0, a.BytesRecorded); } catch { } };
                loop.RecordingStopped += Rec_DeviceStopped;
                recFrames = 0;
                recWatch = System.Diagnostics.Stopwatch.StartNew();
                int rate = lf.SampleRate;
                pump = new System.Threading.Timer(_ =>
                {
                    lock (recLock)
                    {
                        if (writer == null) return;
                        long due = (long)(recWatch.Elapsed.TotalSeconds * rate) - recFrames;
                        if (due <= 0) return;
                        try
                        {
                            var buf = new float[due * 2];
                            int n = mixer.Read(buf, 0, buf.Length);
                            writer.WriteSamples(buf, 0, n);
                            recFrames += n / 2;
                        }
                        catch { }
                    }
                }, null, 50, 50);
                loop.StartRecording();
                StartMic((b, n) => { try { mb.AddSamples(b, 0, n); } catch { } });
            }
        }
        catch (Exception)
        {
            CleanupRecording();
            try { if (File.Exists(recPath)) File.Delete(recPath); } catch { }
            SetStatus(withMusic ? "Не удалось начать запись с музыкой: проверьте звуковое устройство и микрофон" : "Не удалось начать запись. Проверьте микрофон");
            return;
        }
        recording = true;
        recWatch ??= System.Diagnostics.Stopwatch.StartNew();
        if (!withMusic) recWatch = System.Diagnostics.Stopwatch.StartNew();
        MusicTgl.IsEnabled = false; MicBtn.IsEnabled = false;
        RecBtn.Background = (Brush)FindResource("Rec"); RecBtn.Foreground = Brushes.White;
        SetStatus((withMusic ? "Идёт запись: голос и музыка" : "Идёт запись: только микрофон") + " · " + Short(MicName()));
    }

    void CleanupRecording()
    {
        try { micIn?.StopRecording(); } catch { }
        try { loop?.StopRecording(); } catch { }
        pump?.Dispose(); pump = null;
        lock (recLock) { try { writer?.Dispose(); } catch { } writer = null; }
        try { micIn?.Dispose(); } catch { }
        try { loop?.Dispose(); } catch { }
        micIn = null; loop = null; micBuf = null; loopBuf = null; mixer = null; recWatch = null;
    }

    // микрофон или устройство отключили посреди записи — сохраняем, что успело записаться, и говорим об этом
    void Rec_DeviceStopped(object sender, StoppedEventArgs e)
    {
        if (e.Exception == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!recording) return;
            StopRecording();
            SetStatus("Запись остановилась: устройство отключено. Записанное сохранено");
        });
    }

    void StopRecording()
    {
        CleanupRecording();
        recording = false;
        RecBtn.Content = "●  Запись";
        RecBtn.ClearValue(Control.BackgroundProperty); RecBtn.ClearValue(Control.ForegroundProperty);
        MusicTgl.IsEnabled = true; MicBtn.IsEnabled = true;
        SetStatus($"Запись сохранена в разделе «{RecSection}»");
        RefreshAll();
        if (shownSection == RecSection && songs.Count > 0) TrackList.ScrollIntoView(songs[^1]);
    }

    // ---------- клавиши и закрытие ----------

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        // Ctrl/Alt + буква — не наши сочетания (кроме Ctrl+F, Ctrl+стрелки): не перехватываем
        if ((ctrl || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) && e.Key is not (Key.F or Key.Up or Key.Down)) return;
        switch (e.Key)
        {
            case Key.Space: PlayPause(); break;
            case Key.Escape: if (jOut == null) return; StopJingle(); break;
            case Key.N: Next(true); break;
            case Key.B: Prev(); break;
            case Key.F: if (ctrl) SearchBox.Focus(); else FadePause(); break;
            case Key.R: ToggleRecord(); break;
            case Key.M: SetCompact(!cfg.Compact); break;
            case Key.W when !ctrl: ToggleMini(); break;
            case Key.F1 or Key.F2 or Key.F3 or Key.F4:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) SetCue(e.Key - Key.F1); else JumpCue(e.Key - Key.F1);
                break;
            case Key.L when !ctrl: Loop_Click(null, null); break;
            case Key.T when !ctrl: SetRepeat(!repeatOn); break;
            case Key.D when !ctrl: Duck_Click(null, null); break;
            case Key.Enter:
                if (TrackList.SelectedItem is Song s) PlayFromList(s); else return;
                break;
            case Key.Up when ctrl && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift): MoveSelected(-1); break;
            case Key.Down when ctrl && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift): MoveSelected(1); break;
            case Key.Up when ctrl: VolSlider.Value = Math.Min(1, VolSlider.Value + 0.05); break;
            case Key.Down when ctrl: VolSlider.Value = Math.Max(0, VolSlider.Value - 0.05); break;
            default:
                int idx = e.Key >= Key.D1 && e.Key <= Key.D9 ? e.Key - Key.D1
                        : e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;
                if (idx < 0 || e.IsRepeat) return; // зажатая цифра не запускает звук снова и снова
                var j = JingleFiles();
                if (idx < j.Count) PlayJingle(j[idx]);
                break;
        }
        e.Handled = true;
    }


    // ---------- «Петля» и «Поклон» ----------

    void LoopLen_Click(object sender, RoutedEventArgs e)
    {
        cfg.LoopLen = int.Parse((string)((FrameworkElement)sender).Tag);
        SetLoopOn(loopOn);
    }

    void MarkLoopLen()
    {
        foreach (Button b in LoopLenPanel.Children)
        {
            bool active = int.Parse((string)b.Tag) == cfg.LoopLen;
            if (active) b.SetResourceReference(Control.BackgroundProperty, "Text"); else b.Background = Brushes.Transparent;
            b.SetResourceReference(Control.ForegroundProperty, active ? "OnText" : "Muted");
        }
    }

    void SetLoopOn(bool on)
    {
        loopOn = on;
        LoopBtn.IsChecked = on;
        LoopBtn.Content = on ? $"Петля идёт · {cfg.LoopLen} с" : $"Петля {cfg.LoopLen} с";
        MarkLoopLen();
    }

    // повторять кусок трека длиной LoopLen секунд, начиная с текущего места; ещё раз — выключить
    void Loop_Click(object sender, RoutedEventArgs e)
    {
        if (loopOn || cur == null) { SetLoopOn(false); if (cur == null) SetStatus("Петля: сначала включите трек"); return; }
        loopStart = Math.Max(EffStart(cur), Math.Min(cur.Reader.CurrentTime.TotalSeconds, Math.Max(0, EffEnd(cur) - cfg.LoopLen)));
        SetLoopOn(true);
        SetStatus($"Петля {cfg.LoopLen} с с места {Fmt(TimeSpan.FromSeconds(loopStart))}");
    }

    void Loop_RightClick(object sender, MouseButtonEventArgs e)
    {
        var m = new ContextMenu { PlacementTarget = LoopBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var n in new[] { 2, 4, 8, 16, 32 })
        {
            var mi = new MenuItem { Header = $"{n} с", IsChecked = cfg.LoopLen == n };
            mi.Click += (a, b) => { cfg.LoopLen = n; SetLoopOn(loopOn); };
            m.Items.Add(mi);
        }
        m.IsOpen = true;
        e.Handled = true;
    }

    // «поклон»: музыка тише, чтобы говорить в микрофон; ещё раз — вернуть
    void Duck_Click(object sender, RoutedEventArgs e)
    {
        ducking = !ducking;
        DuckBtn.Content = ducking ? "Вернуть звук" : "Поклон: приглушить";
        SetStatus(ducking ? "Музыка приглушена — можно говорить. Вернуть: D" : "Звук возвращается");
    }

    // ---------- предпрослушивание ----------

    void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (preview != null && preview.Active) { StopPreview(); return; }
        Song next = upNext.FirstOrDefault() ?? (cur != null && qi + 1 < queue.Count ? queue[qi + 1] : null);
        if (next == null) { SetStatus("Послушать нечего: дальше треков нет. Правый клик по треку → «Послушать в наушниках»"); return; }
        StartPreview(next);
    }

    void StartPreview(Song s)
    {
        int dev = Preview.Find(cfg.PreviewDeviceName);
        if (dev < 0)
        {
            SetStatus("Сначала выберите наушники: меню ⚙ → «Наушники» (не тот выход, что на зал)");
            return;
        }
        try
        {
            preview ??= new Preview();
            preview.Start(s.Path, dev, (float)cfg.Volume);
            previewName = s.Name;
            PreviewBtn.Content = "Стоп наушники";
            SetStatus($"В наушниках: «{s.Name}»");
        }
        catch
        {
            StopPreview();
            SetStatus("Не удалось включить наушники: проверьте выход и файл");
        }
    }

    void StopPreview()
    {
        preview?.Dispose();
        PreviewBtn.Content = "Послушать";
    }

    // ---------- метки на волне ----------

    // i = -1: первая свободная; иначе — на этот номер (поверх старой)
    void SetCue(int i)
    {
        if (cur == null) { SetStatus("Метка: сначала включите трек"); return; }
        if (i < 0)
        {
            i = Array.FindIndex(cueSec, v => v < 0);
            if (i < 0) { SetStatus("Все 4 метки заняты: правый клик по флажку убирает, Shift+F1…F4 — заменить"); return; }
        }
        cueSec[i] = cur.Reader.CurrentTime.TotalSeconds;
        Cues.Save(cur.Song.Path, cueSec);
        Wave.Cues = cueSec;
        SetStatus($"Метка {i + 1}: {Fmt(TimeSpan.FromSeconds(cueSec[i]))}");
    }

    void JumpCue(int i)
    {
        if (cur == null || i < 0 || i >= cueSec.Length) return;
        if (cueSec[i] < 0) { SetStatus($"Метка {i + 1} не поставлена (Shift+F{i + 1})"); return; }
        cur.SeekClean(Math.Min(cueSec[i], cur.Reader.TotalTime.TotalSeconds - 0.1));
        if (loopOn) SetLoopOn(false);
        // из зоны затухания последнего трека вернулись — звук должен вернуться
        if (cur.NextTriggered && cur.OnZero != null && Remain(cur) > FadeFor(cur) + 0.5)
        { cur.OnZero = null; cur.Factor = 1f; cur.Target = 1f; cur.NextTriggered = false; }
    }

    void RemoveCue(int i)
    {
        if (cur == null || i < 0 || i >= cueSec.Length) return;
        cueSec[i] = -1;
        Cues.Save(cur.Song.Path, cueSec);
        Wave.Cues = cueSec;
        SetStatus($"Метка {i + 1} убрана");
    }

    void Cue_Click(object sender, RoutedEventArgs e) => SetCue(-1);

    // ---------- рейка иконок слева ----------

    void OpenMenu(FrameworkElement at, ContextMenu m)
    {
        m.PlacementTarget = at;
        m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        m.IsOpen = true;
    }

    // «к играющему»: открыть раздел, где играет трек, и показать его в списке
    void RailNow_Click(object sender, RoutedEventArgs e)
    {
        if (cur == null) { SetStatus("Ничего не играет"); return; }
        if (playingSection != null && playingSection != shownSection)
        {
            var it = SectionList.Items.OfType<SectionItem>().FirstOrDefault(x => x.Name == playingSection);
            if (it != null) SectionList.SelectedItem = it;
        }
        var s = songs.FirstOrDefault(x => x.IsPlaying);
        if (s != null) { TrackList.ScrollIntoView(s); TrackList.SelectedItem = s; }
    }

    void RailView_Click(object sender, RoutedEventArgs e) => SetCards(!cfg.Cards);

    void RailHist_Click(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        if (history.Count == 0) m.Items.Add(new MenuItem { Header = "Пока ничего не играло", IsEnabled = false });
        foreach (var h in Enumerable.Reverse(history).ToList())
        {
            var mi = new MenuItem { Header = h.Name };
            var song = h;
            mi.Click += (a, b) =>
            {
                if (!File.Exists(song.Path)) { SetStatus("Файла больше нет"); return; }
                if (Start(song, true)) { playingSection = song.Section ?? shownSection; RebuildQueue(); }
            };
            m.Items.Add(mi);
        }
        OpenMenu(SettingsBtn, m);
    }

    void RailSet_Click(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        var now = new MenuItem { Header = "К играющему треку" };
        now.Click += (a, b) => RailNow_Click(null, null);
        m.Items.Add(now);
        var hist = new MenuItem { Header = "История (последние треки)…" };
        hist.Click += (a, b) => Dispatcher.BeginInvoke(new Action(() => RailHist_Click(null, null)));
        m.Items.Add(hist);
        m.Items.Add(new Separator { Style = (Style)FindResource("MenuSep") });
        foreach (var (title, key) in new[] { ("Тема: светлая", "light"), ("Тема: тёмная", "dark"), ("Тема: как в Windows", "auto") })
        {
            var mi = new MenuItem { Header = title, IsChecked = Theme.Mode == key };
            var k = key;
            mi.Click += (a, b) => { cfg.Theme = k; cfg.Save(); Theme.Apply(k); };
            m.Items.Add(mi);
        }
        m.Items.Add(new Separator { Style = (Style)FindResource("MenuSep") });
        var phones = new MenuItem { Header = "Наушники (не тот выход, что на зал)" };
        var devs = Preview.Devices();
        var off = new MenuItem { Header = "Не выбраны", IsChecked = Preview.Find(cfg.PreviewDeviceName) < 0 };
        off.Click += (a, b) => { cfg.PreviewDeviceName = null; cfg.Save(); StopPreview(); };
        phones.Items.Add(off);
        for (int di = 0; di < devs.Count; di++)
        {
            var d = di;
            var it = new MenuItem { Header = devs[di], IsChecked = cfg.PreviewDeviceName == devs[di] };
            it.Click += (a, b) => { cfg.PreviewDeviceName = devs[d]; cfg.Save(); SetStatus($"Наушники: {devs[d]}"); };
            phones.Items.Add(it);
        }
        m.Items.Add(phones);
        var wave = new MenuItem { Header = cfg.WaveBars ? "Волна: заливкой" : "Волна: столбиками" };
        wave.Click += (a, b) => SetWaveStyle(cfg.WaveBars);
        var top = new MenuItem { Header = "Поверх окон", IsChecked = TopTgl.IsChecked == true };
        top.Click += (a, b) => { TopTgl.IsChecked = !(TopTgl.IsChecked == true); Top_Click(null, null); };
        var mini = new MenuItem { Header = "Мини-окно" };
        mini.Click += (a, b) => ToggleMini();
        m.Items.Add(wave); m.Items.Add(top); m.Items.Add(mini);
        OpenMenu(SettingsBtn, m);
    }

    // ---------- обрезка: метки «Начало» и «Конец» на волне ----------

    bool trimEdit;

    // с какой секунды и до какой секунды играет трек (в режиме обрезки — целиком)
    double EffStart(Slot s) => trimEdit || s.TrimStart < 0 ? 0 : s.TrimStart;

    double EffEnd(Slot s)
    {
        double tot = s.Reader.TotalTime.TotalSeconds;
        return !trimEdit && s.TrimEnd > 0 ? Math.Min(s.TrimEnd, tot) : tot;
    }

    double Remain(Slot s) => EffEnd(s) - s.Reader.CurrentTime.TotalSeconds;

    void ShowTrim()
    {
        if (cur == null)
        {
            Wave.SetTrim(-1, -1, false);
            TrimBar.Visibility = Visibility.Collapsed;
            ZoomBox.Visibility = Visibility.Collapsed;
            Plate.BorderBrush = (Brush)FindResource("Line"); Plate.BorderThickness = new Thickness(1);
            RightPanel.Visibility = Visibility.Visible; RightCol.Width = new GridLength(268); GapCol.Width = new GridLength(16);
            JingleRow.Visibility = Visibility.Visible;
            NowCard.Visibility = cfg.Compact ? Visibility.Collapsed : Visibility.Visible;
            TrimBtn.Content = "Обрезка";
            return;
        }
        Wave.SetTrim(cur.TrimStart, cur.TrimEnd, trimEdit);
        TrimBar.Visibility = trimEdit ? Visibility.Visible : Visibility.Collapsed;
        JingleRow.Visibility = trimEdit ? Visibility.Collapsed : Visibility.Visible; // место под панель обрезки
        ZoomBox.Visibility = TrimBar.Visibility;
        Plate.BorderBrush = (Brush)FindResource(trimEdit ? "Accent" : "Line"); // рамка всей плиты: видно издалека, что идёт обрезка
        Plate.BorderThickness = new Thickness(trimEdit ? 2.5 : 1);
        RightPanel.Visibility = trimEdit ? Visibility.Collapsed : Visibility.Visible; // «Далее», петля и «Поклон» на время обрезки не нужны
        RightCol.Width = new GridLength(trimEdit ? 0 : 268); GapCol.Width = new GridLength(trimEdit ? 0 : 16);
        NowCard.Visibility = trimEdit || cfg.Compact ? Visibility.Collapsed : Visibility.Visible; // и под приближенную волну
        bool has = cur.TrimStart >= 0 || cur.TrimEnd > 0;
        TrimBtn.Content = trimEdit ? "Обрезка: идёт" : has ? "Обрезка ✂" : "Обрезка";
        if (trimEdit)
        {
            string a = cur.TrimStart >= 0 ? Fmt(TimeSpan.FromSeconds(cur.TrimStart)) : "не задано";
            string b = cur.TrimEnd > 0 ? Fmt(TimeSpan.FromSeconds(cur.TrimEnd)) : "не задан";
            TrimHint.Text = $"Начало: {a}  ·  Конец: {b}";
            StartBtn.Content = "Начало  " + (cur.TrimStart >= 0 ? a : "—");
            EndBtn.Content = "Конец  " + (cur.TrimEnd > 0 ? b : "—");
            zoomWinLen = -1; // заново подобрать окно
            UpdateZoom(cur.Reader.CurrentTime.TotalSeconds);
        }
    }

    void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (trimEdit) { TrimDone_Click(null, null); return; }
        if (cur == null)
        {
            var s = TrackList.SelectedItem as Song ?? songs.FirstOrDefault();
            if (s == null) { SetStatus("Сначала выберите трек в списке"); return; }
            EnterTrimFor(s);
            return;
        }
        EnterTrim();
    }

    void EnterTrim()
    {
        if (cur == null) return;
        if (cfg.Compact) SetCompact(false); // волна нужна на виду
        trimEdit = true;
        ShowTrim();
        SetStatus("Обрезка: включите трек и нажмите «Начало» и «Конец»");
    }

    // из меню трека: загружаем его в плеер на паузе и включаем режим обрезки
    void EnterTrimFor(Song s)
    {
        if (cur == null || cur.Song.Path != s.Path)
        {
            PlayFromList(s);
            if (cur == null || cur.Song.Path != s.Path) return;
            cur.OnZero = null; cur.Target = cur.Factor;
            cur.Out.Pause();
        }
        EnterTrim();
    }

    // ---------- приближённая волна ----------

    static readonly double[] ZoomLevels = { 30, 15, 8, 4, 2 }; // секунд в окне
    int zoomIdx = 2;
    double zoomStart, zoomWinLen = -1;
    bool zoomUser;   // игла поставлена пальцем/мышью: окно не переезжает само

    void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomStep(1);
    void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomStep(-1);

    void ZoomStep(int d)
    {
        int n = Math.Clamp(zoomIdx + d, 0, ZoomLevels.Length - 1);
        if (n == zoomIdx) return;
        zoomIdx = n;
        zoomWinLen = -1;
        zoomUser = false;
        if (trimEdit) ShowTrim();
    }

    // окно приближения: игла в нём; при игре окно «перелистывается», когда игла доходит до правого края
    void UpdateZoom(double pos)
    {
        if (cur == null) return;
        double tot = cur.Reader.TotalTime.TotalSeconds;
        double W = Math.Min(ZoomLevels[zoomIdx], Math.Max(1.5, tot));
        bool playing = cur.Out.PlaybackState == PlaybackState.Playing;
        bool atEnd = zoomStart >= tot - W - 0.001; // окно уже упёрлось в конец трека: дальше двигать некуда (иначе пересчёт каждый такт)
        bool recentre = zoomWinLen < 0 || zoomWinLen != W || pos < zoomStart - 0.001 || pos > zoomStart + W
                        || (!zoomUser && playing && !atEnd && pos > zoomStart + W * 0.85);
        if (recentre)
        {
            zoomStart = Math.Clamp(pos - W * 0.35, 0, Math.Max(0, tot - W)); // игла левее центра: больше видно вперёд
            zoomWinLen = W;
            zoomUser = false;
            RequestZoomPeaks(zoomStart, W);
        }
        ZoomWave.Duration = W;
        ZoomWave.TimeOffset = zoomStart;
        ZoomWave.FadeSeconds = 0;
        if (!ZoomWave.Dragging) ZoomWave.Progress = Math.Clamp((pos - zoomStart) / W, 0, 1);
        var zc = cueSec.Select(c => c < 0 || c < zoomStart || c > zoomStart + W ? -1.0 : c - zoomStart).ToArray();
        if (ZoomWave.Cues == null || !ZoomWave.Cues.SequenceEqual(zc)) ZoomWave.Cues = zc; // без лишней перерисовки каждый такт
        double a = cur.TrimStart < 0 || cur.TrimStart < zoomStart ? -1 : Math.Min(W, cur.TrimStart - zoomStart);
        double b = cur.TrimEnd <= 0 || cur.TrimEnd > zoomStart + W ? -1 : Math.Max(0.001, cur.TrimEnd - zoomStart);
        ZoomWave.SetTrim(a, b, true);
    }

    CancellationTokenSource zoomCts;
    string zoomCachePath; double zoomCacheStart = -1, zoomCacheLen; float[] zoomCache;

    // пики окна считаются в одном фоновом запросе; новый запрос отменяет прежний, уже посчитанное окно берётся из памяти
    async void RequestZoomPeaks(double start, double len)
    {
        var slot = cur;
        if (slot == null) return;
        string path = slot.Song.Path;
        zoomCts?.Cancel();
        if (zoomCache != null && zoomCachePath == path && Math.Abs(zoomCacheStart - start) < 0.01 && Math.Abs(zoomCacheLen - len) < 0.01)
        {
            ZoomWave.Peaks = zoomCache; ZoomWave.Loading = false;
            return;
        }
        var cts = zoomCts = new CancellationTokenSource();
        ZoomWave.Loading = true;
        ZoomWave.Peaks = null; // старые пики не показываем под новым окном
        float[] peaks;
        try
        {
            peaks = await Task.Run(() => WaveformCache.Window(path, start, len, 1000, cts.Token), cts.Token);
            if (peaks == null && !cts.IsCancellationRequested) // не вышло с первого раза (файл занят): ещё одна попытка
            {
                await Task.Delay(300, cts.Token);
                peaks = await Task.Run(() => WaveformCache.Window(path, start, len, 1000, cts.Token), cts.Token);
                if (peaks == null && !cts.IsCancellationRequested) App.LogCrash("ZoomPeaks", new Exception($"окно волны не посчиталось: {path} {start:0.0}+{len:0.0}"));
            }
        }
        catch (Exception) { return; }
        if (cts.IsCancellationRequested) return;
        if (cur != slot) { ZoomWave.Loading = false; return; }
        if (peaks != null) { zoomCachePath = path; zoomCacheStart = start; zoomCacheLen = len; zoomCache = peaks; }
        ZoomWave.Peaks = peaks;
        ZoomWave.Loading = false;
    }

    void ZoomScrub(double frac, bool done)
    {
        if (cur == null) return;
        if (!done) return;
        double tot = cur.Reader.TotalTime.TotalSeconds;
        double t = zoomStart + frac * Math.Max(0.1, zoomWinLen);
        cur.SeekClean(Math.Clamp(t, 0, Math.Max(0, tot - 0.1)));
        zoomUser = true;
    }

    void TrimStart_Click(object sender, RoutedEventArgs e) => MarkTrim(true);
    void TrimEnd_Click(object sender, RoutedEventArgs e) => MarkTrim(false);

    void MarkTrim(bool isStart)
    {
        if (cur == null) { SetStatus("Сначала включите трек"); return; }
        double pos = Math.Round(cur.Reader.CurrentTime.TotalSeconds, 2);
        if (isStart)
        {
            if (cur.TrimEnd > 0 && pos >= cur.TrimEnd - 0.5) { SetStatus("Начало должно быть раньше конца: сначала передвиньте «Конец»"); return; }
            cur.TrimStart = pos;
        }
        else
        {
            if (cur.TrimStart >= 0 && pos <= cur.TrimStart + 0.5) { SetStatus("Конец должен быть позже начала"); return; }
            cur.TrimEnd = pos;
        }
        Trims.Save(cur.Song.Path, cur.TrimStart, cur.TrimEnd);
        ShowTrim();
        SetStatus($"{(isStart ? "Начало" : "Конец")} трека: {Fmt(TimeSpan.FromSeconds(pos))}");
    }

    void TrimReset_Click(object sender, RoutedEventArgs e)
    {
        if (cur == null) return;
        cur.TrimStart = -1; cur.TrimEnd = -1;
        Trims.Save(cur.Song.Path, -1, -1);
        ShowTrim();
        SetStatus("Обрезка сброшена: трек играет целиком");
    }

    void TrimDone_Click(object sender, RoutedEventArgs e)
    {
        trimEdit = false;
        ShowTrim();
        if (cur != null)
        {
            double p = cur.Reader.CurrentTime.TotalSeconds;
            if (p < EffStart(cur) || p > EffEnd(cur) - 0.1) cur.SeekClean(EffStart(cur));
            if (cur.TrimStart >= 0 || cur.TrimEnd > 0) SetStatus("Обрезка сохранена: трек играет от «Начало» до «Конец»");
        }
    }

    void ResetTrim(Song s)
    {
        Trims.Save(s.Path, -1, -1);
        if (cur != null && cur.Song.Path == s.Path) { cur.TrimStart = -1; cur.TrimEnd = -1; ShowTrim(); }
        SetStatus($"Обрезка «{s.Name}» сброшена");
    }

    // обрезанную версию можно сохранить отдельным файлом (оригинал не меняется)
    async void SaveTrimmed(Song s)
    {
        var t = Trims.Get(s.Path);
        try
        {
            double total = Trimmer.Length(s.Path).TotalSeconds;
            double a = t[0] >= 0 ? t[0] : 0, b = t[1] > 0 ? Math.Min(t[1], total) : total;
            var stem = string.Concat(System.IO.Path.GetFileNameWithoutExtension(s.Path).Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var dest = Trimmer.UniquePath(System.IO.Path.GetDirectoryName(s.Path), stem + " (обрезка)" + Trimmer.OutExt(s.Path));
            SetStatus("Сохраняю обрезанный трек…");
            await Task.Run(() => Trimmer.Trim(s.Path, a, b, dest));
            RefreshAll();
            SetStatus($"Сохранено: «{System.IO.Path.GetFileNameWithoutExtension(dest)}»");
        }
        catch (Exception ex) { SetStatus("Не получилось сохранить: " + ex.Message); }
    }

    // ---------- узкий плеер ----------

    void Collapse_Click(object sender, RoutedEventArgs e) => SetCompact(!cfg.Compact);

    // ---------- вид волны ----------

    void WaveStyle_Click(object sender, RoutedEventArgs e) => SetWaveStyle(cfg.WaveBars);

    void SetWaveStyle(bool fill)
    {
        cfg.WaveBars = !fill;
        Wave.Fill = fill;
        if (mini != null) mini.Wave.Fill = fill;
        WaveStyleBtn.Content = fill ? "Волна: заливка" : "Волна: столбики";
    }

    // ---------- плавающий мини-виджет ----------

    MiniWindow mini;
    bool closing;

    void Mini_Click(object sender, RoutedEventArgs e) => ToggleMini();

    void ToggleMini()
    {
        if (mini != null) { mini.Close(); return; }
        OpenMini();
    }

    void OpenMini()
    {
        var w = new MiniWindow();
        w.PlayClicked += PlayPause;
        w.NextClicked += () => Next(true);
        w.PrevClicked += Prev;
        w.Scrub += Wave_Scrub;
        w.ExpandClicked += () =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        };
        w.Closed += (s, e) =>
        {
            cfg.MiniLeft = w.Left; cfg.MiniTop = w.Top;
            if (!closing) cfg.MiniOpen = false;
            mini = null;
        };
        // место запоминаем, но не даём окну уйти за край экрана (монитор могли отключить)
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        double l = cfg.MiniLeft ?? vs.Right - w.Width - 40, t = cfg.MiniTop ?? vs.Bottom - w.Height - 80;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = Math.Clamp(l, vs.Left, Math.Max(vs.Left, vs.Right - w.Width));
        w.Top = Math.Clamp(t, vs.Top, Math.Max(vs.Top, vs.Bottom - w.Height));
        mini = w;
        w.Wave.Fill = !cfg.WaveBars;
        cfg.MiniOpen = true;
        w.Show();
        SyncMini();
    }

    // ---------- вид списка: строки или карточки ----------

    void ViewList_Click(object sender, RoutedEventArgs e) => SetCards(false);
    void ViewCards_Click(object sender, RoutedEventArgs e) => SetCards(true);

    void SetCards(bool on)
    {
        cfg.Cards = on;
        TrackList.ItemContainerStyle = (Style)FindResource(on ? "TrackCard" : "TrackItem");
        // выбранный вид — залит акцентом
        foreach (var (b, active) in new[] { (ViewCards, on), (ViewList, !on) })
        {
            if (active) b.SetResourceReference(Control.BackgroundProperty, "Text"); else b.Background = Brushes.Transparent;
            b.SetResourceReference(Control.ForegroundProperty, active ? "OnText" : "Muted");
        }
        if (ready) cfg.Save();
    }

    void SetCompact(bool on)
    {
        cfg.Compact = on;
        FullPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        NowCard.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        MiniPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Plate.Padding = on ? new Thickness(18, 10, 18, 10) : new Thickness(26, 16, 26, 16);
        Plate.CornerRadius = new CornerRadius(on ? 24 : 28);
        // одна и та же волна живёт то в большой, то в узкой панели
        FullWaveHost.Child = null;
        MiniWaveHost.Child = null;
        if (on) { Wave.ShowAxis = false; Wave.Height = 30; MiniWaveHost.Child = Wave; }
        else { Wave.ShowAxis = true; Wave.Height = 88; FullWaveHost.Child = Wave; }
        SyncMini();
    }

    void SyncMini()
    {
        MiniNow.Text = NowText.Text;
        MiniRemain.Text = RemainText.Text;
        MiniRemain.Foreground = RemainText.Foreground;
        MiniPlay.Content = PlayBtn.Content;
        var info = recording ? "● идёт запись" : StatusText.Text;
        MiniInfo.Text = info;
        MiniInfo.Foreground = (Brush)FindResource(recording ? "Rec" : "Muted");
        MiniInfo.Visibility = string.IsNullOrEmpty(info) ? Visibility.Collapsed : Visibility.Visible;
        mini?.Sync(NowText.Text, RemainText.Text, RemainText.Foreground, Wave.Progress, (string)PlayBtn.Content, Wave.Peaks);
    }

    // ---------- стекло (акрил Windows 11) ----------

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    bool glass;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
        try
        {
            int acrylic = 3;
            glass = DwmSetWindowAttribute(hwnd, 38, ref acrylic, 4) == 0;
        }
        catch { }
        if (glass && src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
        ApplyChrome();
        MarkThemeButtons();
        MarkLoopLen();
        Theme.Changed += () => { ApplyChrome(); MarkThemeButtons(); MarkLoopLen(); };
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        cfg.Theme = (string)((FrameworkElement)sender).Tag;
        cfg.Save();
        Theme.Apply(cfg.Theme);
        MarkThemeButtons();
    }

    void MarkThemeButtons()
    {
        foreach (var b in new[] { ThemeLight, ThemeDark, ThemeAuto })
        {
            bool active = (string)b.Tag == Theme.Mode;
            if (active) b.SetResourceReference(Control.BackgroundProperty, "Text"); else b.Background = Brushes.Transparent;
            b.SetResourceReference(Control.ForegroundProperty, active ? "OnText" : "Muted");
            b.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // цвет рамки/стекла под тему; без стекла (старая Windows) — сплошной фон
    void ApplyChrome()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = Theme.IsLight ? 0 : 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
        }
        catch { }
        if (!glass) Root.Background = new SolidColorBrush(Theme.IsLight ? Color.FromRgb(0xE9, 0xEC, 0xF1) : Color.FromRgb(0x14, 0x15, 0x19));
        else Root.SetResourceReference(Panel.BackgroundProperty, "WinTint");
    }

    // ---------- своя полоса заголовка ----------

    // окно уже/ниже «родного» размера (половина экрана на ноутбуке): всё содержимое уменьшаем целиком, а не вылезаем за экран
    double uiScale = 1;

    void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double k = Math.Max(0.6, Math.Min(1.0, Math.Min(ActualWidth / 1080.0, ActualHeight / 800.0)));
        if (Math.Abs(uiScale - k) < 0.005) return;
        uiScale = k;
        Root.LayoutTransform = k >= 0.999 ? Transform.Identity : new ScaleTransform(k, k);
        var wc = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (wc != null) wc.CaptionHeight = 56 * k;
    }

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        var max = WindowState == WindowState.Maximized;
        // в развёрнутом виде окно без рамки вылезает за экран на толщину рамки — отодвигаем содержимое
        Root.Margin = max ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left + 2) : new Thickness(0);
        MaxBtn.Content = max ? "" : "";
        MaxBtn.ToolTip = max ? "Свернуть в окно" : "Развернуть";
    }

    void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        timer.Stop();
        closing = true;
        StopJingle();
        mini?.Close();
        preview?.Dispose();
        if (recording) StopRecording();
        cur?.Dispose();
        cur = null;
        foreach (var f in fading) f.Dispose();
        fading.Clear();
        cfg.Save();
    }
}
