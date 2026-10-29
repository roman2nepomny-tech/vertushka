using System.Windows;

namespace Vertushka;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Theme.Apply(Settings.Load().Theme);
        base.OnStartup(e);
    }
}
