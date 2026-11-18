using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace UProAttendance.Presentation;

internal static class TerminalSetup
{
    public static void Show(Window owner, OperationalTerminal terminal, CancellationToken cancellation)
    {
        var identity = terminal.Device;
        var dialog = new Window
        {
            Owner = owner,
            Title = "Configurar terminal UProCheck",
            Width = 495,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var content = new StackPanel { Margin = new Thickness(22) };
        dialog.Content = content;

        TextBlock Label(string text)
        {
            var label = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 3)
            };
            content.Children.Add(label);
            return label;
        }

        var identifier = Label("");
        Label("Origen HTTPS de UProApp confirmado por tu administrador");
        var url = new TextBox { Text = identity?.Url.AbsoluteUri ?? "", IsReadOnly = identity is not null };
        content.Children.Add(url);

        Label("Nombre del dispositivo");
        var name = new TextBox { Text = identity?.Nombre ?? "", MaxLength = 100, IsReadOnly = identity is not null };
        content.Children.Add(name);

        var replacement = new CheckBox
        {
            Content = "Usar credencial individual emitida por UProApp",
            IsChecked = identity?.DeviceToken is not null,
            IsEnabled = identity is { DeviceToken: null },
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = identity is null ? Visibility.Collapsed : Visibility.Visible
        };
        content.Children.Add(replacement);
        var secretLabel = Label("");
        var secret = new PasswordBox { MaxLength = 512 };
        content.Children.Add(secret);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
        content.Children.Add(status);
        var save = new Button { Content = "Guardar y verificar", IsDefault = true };
        content.Children.Add(save);

        void RefreshIdentity()
        {
            var device = terminal.Device;
            identifier.Visibility = device is null ? Visibility.Collapsed : Visibility.Visible;
            identifier.Text = device is null ? "" : $"Identificador de la terminal\n{device.DeviceId:D}";
            if (device is null)
                return;
            url.Text = device.Url.AbsoluteUri;
            url.IsReadOnly = true;
            name.Text = device.Nombre;
            name.IsReadOnly = true;
            replacement.Visibility = Visibility.Visible;
        }

        void UpdateSecretLabel() => secretLabel.Text = replacement.IsChecked == true
            ? "Credencial individual" : "Token temporal de autorregistro";
        replacement.Checked += (_, _) => UpdateSecretLabel();
        replacement.Unchecked += (_, _) => UpdateSecretLabel();
        RefreshIdentity();
        UpdateSecretLabel();
        dialog.Closing += (_, e) => e.Cancel = !save.IsEnabled && !cancellation.IsCancellationRequested;
        dialog.Closed += (_, _) => secret.Clear();
        save.Click += async (_, _) =>
        {
            var value = secret.Password.Trim();
            secret.Clear();
            var useReplacement = replacement.IsChecked == true;
            if (value.Length == 0 || !useReplacement && !value.StartsWith("upc_r_", StringComparison.Ordinal))
            {
                status.Text = useReplacement ? "Introduce la credencial emitida por UProApp."
                    : "Introduce el token temporal generado en UProApp.";
                return;
            }

            save.IsEnabled = false;
            status.Text = "Verificando con UProApp…";
            var origin = url.Text;
            var label = name.Text;
            try
            {
                await Task.Run(() => terminal.ConfigureAsync(origin, label, value, useReplacement, cancellation), cancellation);
                save.IsEnabled = true;
                dialog.Close();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (TerminalException error)
            {
                status.Text = error.Message == "DEVICE_ALREADY_REGISTERED"
                    ? "El UUID ya existe. Busca la terminal en UProApp y rota su credencial."
                    : error.Transient && !useReplacement
                        ? "El resultado del alta es desconocido. Busca el UUID en UProApp antes de reintentar."
                        : $"UProApp no pudo verificar la terminal ({error.Message}).";
            }
            catch (ArgumentException error) { status.Text = error.Message; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                status.Text = "No se pudo guardar la configuración local.";
            }
            finally
            {
                value = "";
                save.IsEnabled = true;
                RefreshIdentity();
            }
        };
        dialog.ShowDialog();
    }
}
