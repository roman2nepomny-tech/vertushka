using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Vertushka;

// Тёмная / светлая / авто (как в Windows). Кисти в App.xaml подменяются на лету.
public static class Theme
{
    public static string Mode { get; private set; } = "dark";
    public static bool IsLight { get; private set; }
    public static event Action Changed;

    // концепт 8 «Графитовый туман»: графит и стекло, акцент — светлое золото (тёмная) / тёмно-синий (светлая)
    static readonly Dictionary<string, string> Dark = new()
    {
        ["Panel"] = "#12D6E2F5", ["Panel2"] = "#1AD6E2F5", ["Hover"] = "#2ED6E2F5",
        ["Text"] = "#EEF0F3", ["Muted"] = "#B2BCCB", ["Accent"] = "#E3D6A8", ["AccentDim"] = "#33E3D6A8",
        ["Rec"] = "#FF5A4D", ["OnAccent"] = "#1E1A08", ["OnText"] = "#0F1217", ["MenuBg"] = "#FF1A1F28", ["WinTint"] = "#F40F1217",
        ["Line"] = "#26D6E2F5", ["Track"] = "#26D6E2F5", ["ThumbBg"] = "#40D6E2F5", ["SelBorder"] = "#80D6E2F5",
        ["RecBg"] = "#33FF5A4D", ["RecText"] = "#FFB0A8", ["WaveRest"] = "#4AEEF0F3", ["MiniBg"] = "#F2121620",
        ["Blob1C"] = "#BF4A6384", ["Blob2C"] = "#B32B3A52", ["Blob3C"] = "#59D8C9A0",
    };

    // светлая: плиты почти непрозрачные, акцент и приглушённый текст темнее (контраст не ниже 4,5:1)
    static readonly Dictionary<string, string> Light = new()
    {
        ["Panel"] = "#A6FFFFFF", ["Panel2"] = "#1A20242C", ["Hover"] = "#2E20242C",
        ["Text"] = "#20242C", ["Muted"] = "#4F5B6E", ["Accent"] = "#2F4A73", ["AccentDim"] = "#2E2F4A73",
        ["Rec"] = "#D6453A", ["OnAccent"] = "#F3F6FB", ["OnText"] = "#F3F6FB", ["MenuBg"] = "#FFF7F8FA", ["WinTint"] = "#F5E5E7EB",
        ["Line"] = "#3320242C", ["Track"] = "#2620242C", ["ThumbBg"] = "#4020242C", ["SelBorder"] = "#802F4A73",
        ["RecBg"] = "#26D6453A", ["RecText"] = "#A8281F", ["WaveRest"] = "#3320242C", ["MiniBg"] = "#F2EEF0F4",
        ["Blob1C"] = "#D9B9C6D8", ["Blob2C"] = "#D9E6DFC9", ["Blob3C"] = "#E6FFFFFF",
    };

    static bool inited;

    public static bool SystemIsLight()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v != 0; }
        catch { return false; }
    }

    public static void Apply(string mode)
    {
        if (mode != "light" && mode != "auto") mode = "dark";
        Mode = mode;
        if (!inited)
        {
            inited = true;
            SystemEvents.UserPreferenceChanged += (_, _) =>
            {
                if (Mode == "auto" && SystemIsLight() != IsLight) Application.Current?.Dispatcher.BeginInvoke(() => Apply("auto"));
            };
        }
        IsLight = mode == "light" || (mode == "auto" && SystemIsLight());
        var set = IsLight ? Light : Dark;
        var res = Application.Current.Resources;
        foreach (var kv in set)
        {
            var c = (Color)ColorConverter.ConvertFromString(kv.Value);
            if (kv.Key.StartsWith("Blob")) res[kv.Key] = c; else res[kv.Key] = new SolidColorBrush(c);
        }
        Changed?.Invoke();
    }

    public static Brush Get(string key) => (Brush)Application.Current.Resources[key];
}
