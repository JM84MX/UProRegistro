using System.IO;
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
        try
        {
            MainWindow = new MainWindow(laboratory);
            MainWindow.Show();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TerminalException or ArgumentException)
        {
            MessageBox.Show("No se pudo abrir o validar el almacenamiento de UProCheck. Revisa la cuenta de Windows, "
                + "si ya hay otra instancia y los archivos en %LOCALAPPDATA%\\UProRegistro\\Terminal\\Operacion.",
                "UProCheck", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
