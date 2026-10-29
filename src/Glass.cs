using System.Windows;

namespace Vertushka;

// Отметка «кнопка выбрана»: включает блик «стекла» в шаблоне кнопки.
public static class Glass
{
    public static readonly DependencyProperty OnProperty =
        DependencyProperty.RegisterAttached("On", typeof(bool), typeof(Glass), new PropertyMetadata(false));
    public static bool GetOn(DependencyObject d) => (bool)d.GetValue(OnProperty);
    public static void SetOn(DependencyObject d, bool v) => d.SetValue(OnProperty, v);
}
