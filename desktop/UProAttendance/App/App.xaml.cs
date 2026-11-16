using System.Windows;

namespace UProAttendance;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var laboratory = false;
#if DEBUG
        laboratory = e.Args.Contains("--laboratorio", StringComparer.Ordinal);
#endif
        MainWindow = new MainWindow(laboratory);
        MainWindow.Show();
    }
}
