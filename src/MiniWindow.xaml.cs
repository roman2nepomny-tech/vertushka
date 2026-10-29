using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Vertushka;

// Плавающий мини-виджет: название, время до конца, волна и три кнопки. Всей логикой управляет MainWindow.
public partial class MiniWindow : Window
{
    public event Action PlayClicked, NextClicked, PrevClicked, ExpandClicked;
    public event Action<double, bool> Scrub;

    public MiniWindow()
    {
        InitializeComponent();
        Wave.Scrub += (f, done) => Scrub?.Invoke(f, done);
    }

    public void Sync(string name, string remain, Brush remainBrush, double progress, string playGlyph, float[] peaks)
    {
        NameText.Text = name;
        RemainText.Text = remain;
        RemainText.Foreground = remainBrush;
        if ((string)PlayBtn.Content != playGlyph) PlayBtn.Content = playGlyph;
        if (!ReferenceEquals(Wave.Peaks, peaks)) Wave.Peaks = peaks;
        if (!Wave.Dragging) Wave.Progress = progress;
    }

    // тянуть окно за любое место, кроме кнопок и волны (волна сама ловит мышь для перемотки)
    void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : null)
            if (d is System.Windows.Controls.Primitives.ButtonBase || d is WaveformControl) return;
        try { DragMove(); } catch { }
    }

    void Play_Click(object sender, RoutedEventArgs e) => PlayClicked?.Invoke();
    void Next_Click(object sender, RoutedEventArgs e) => NextClicked?.Invoke();
    void Prev_Click(object sender, RoutedEventArgs e) => PrevClicked?.Invoke();
    void Expand_Click(object sender, RoutedEventArgs e) => ExpandClicked?.Invoke();
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
