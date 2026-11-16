using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using UProAttendance.Core;
using UProAttendance.Hardware;
using UProAttendance.Presentation;

namespace UProAttendance;

public partial class MainWindow : Window
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-MX");
    private static readonly TimeZoneInfo MexicoCity = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");
    private static readonly (Finger Finger, string Label)[] FingerLabels =
    [
        (Finger.RightThumb, "Pulgar derecho"), (Finger.RightIndex, "Índice derecho"),
        (Finger.RightMiddle, "Medio derecho"), (Finger.RightRing, "Anular derecho"),
        (Finger.RightLittle, "Meñique derecho"), (Finger.LeftThumb, "Pulgar izquierdo"),
        (Finger.LeftIndex, "Índice izquierdo"), (Finger.LeftMiddle, "Medio izquierdo"),
        (Finger.LeftRing, "Anular izquierdo"), (Finger.LeftLittle, "Meñique izquierdo")
    ];

    private readonly SessionTemplateGallery gallery = new();
    private readonly Biometrics biometrics;
    private readonly ReaderWorkflow workflow;
    private readonly IEnrollmentAuthorization authorization;
    private readonly IEmployeeProvider employees;
    private readonly AttendanceSubmission attendance;
    private readonly bool laboratory;
    private readonly CancellationTokenSource lifetime = new();
    private Task? screenWork;
    private CancellationTokenSource? screenCancellation;
    private IEnrollmentSession? enrollmentSession;
    private HwndSource? windowSource;
    private ReaderState reader = new(ReaderStatus.SdkMissing);
    private Identification? identified;
    private long identifiedAt;
    private bool administrative;
    private bool navigating;
    private bool enrolling;
    private bool submitting;
    private bool closing;
    private bool allowClose;

    public MainWindow(bool laboratory)
    {
        this.laboratory = laboratory;
        biometrics = new(gallery);
        workflow = new(biometrics);
#if DEBUG
        if (laboratory)
        {
            var debugIntegration = new Laboratory();
            authorization = debugIntegration;
            employees = debugIntegration;
            attendance = new(debugIntegration);
        }
        else
#endif
        {
            var integration = new UnconfiguredUproApp();
            authorization = integration;
            employees = integration;
            attendance = new(integration);
        }

        InitializeComponent();
        LabBadge.Visibility = laboratory ? Visibility.Visible : Visibility.Collapsed;
        Fingers.ItemsSource = FingerLabels.Select(item => new FingerOption(item.Finger, item.Label)).ToArray();
        SampleBar.Maximum = Biometrics.SampleCount;
        Loaded += OnLoaded;
        Closing += CloseAfterWork;
        SizeChanged += (_, e) => SetCompactLayout(e.NewSize.Height < 620);
    }

    internal FlowState State { get; private set; } = FlowState.Idle;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var available = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, available.Width));
        Height = Math.Max(MinHeight, Math.Min(Height, available.Height));
    }

    private void SetCompactLayout(bool compact)
    {
        HeaderRow.Height = new GridLength(compact ? 56 : 72);
        ContentRoot.Margin = compact ? new Thickness(24, 10, 24, 10) : new Thickness(34, 25, 34, 24);
        PageIntro.Margin = new Thickness(0, 0, 0, compact ? 12 : 24);
        TitleText.FontSize = compact ? 34 : 43;
        ScannerCard.MinHeight = compact ? 265 : 375;
        ScannerCard.Padding = compact ? new Thickness(14, 10, 14, 10) : new Thickness(18, 23, 18, 22);
        ScannerVisual.Height = compact ? 132 : 198;
        ScannerVisual.Margin = new Thickness(0, 0, 0, compact ? 6 : 19);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        windowSource = (HwndSource)PresentationSource.FromVisual(this);
        windowSource.AddHook(ReadDeviceMessage);
        StartScreenWork(AttendanceLoopAsync);
    }

    private IntPtr ReadDeviceMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x8100 && wParam == IntPtr.Zero) // FDx Auto-On: SGDEVEVNET_FINGER_OFF
            workflow.FingerLifted();
        return IntPtr.Zero;
    }

    private void StartScreenWork(Func<CancellationToken, Task> run)
    {
        screenCancellation?.Dispose();
        screenCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        screenWork = RunScreenWorkAsync(run, screenCancellation.Token);
    }

    private static async Task RunScreenWorkAsync(Func<CancellationToken, Task> run, CancellationToken cancellation)
    {
        try
        {
            await run(cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task DrainScreenAsync()
    {
        screenCancellation?.Cancel();
        if (screenWork is not null)
            await screenWork;
        screenCancellation?.Dispose();
        screenCancellation = null;
        screenWork = null;
    }

    private async void Navigate(object sender, RoutedEventArgs e)
    {
        if (navigating || closing || submitting)
            return;

        navigating = true;
        NavButton.IsEnabled = false;
        try
        {
            await DrainScreenAsync();
            if (closing)
                return;
            enrollmentSession?.Dispose();
            enrollmentSession = null;
            Employees.ItemsSource = null;
            Fingers.SelectedIndex = -1;
            ClearIdentity();
            PaintProgress(0);
            administrative = !administrative;
            AttendancePanel.Visibility = administrative ? Visibility.Collapsed : Visibility.Visible;
            EnrollmentPanel.Visibility = administrative ? Visibility.Visible : Visibility.Collapsed;
            ProgressPanel.Visibility = administrative ? Visibility.Visible : Visibility.Collapsed;

            if (administrative)
            {
                TitleText.Text = "Registrar huellas";
                Subtitle.Text = "Configura dos dedos por estilista y, si hace falta, un tercero.";
                NavButton.Content = "←  Registro de asistencia";
                AdminTitle.Text = "Acceso administrativo";
                AdminDetail.Text = laboratory
                    ? "Usa una sesión de Windows con privilegios de administrador para acceder a los datos de prueba."
                    : "La verificación de administradores y la lista de estilistas requieren una conexión autorizada con UProApp.";
                AuthorizeButton.Visibility = Visibility.Visible;
                EnrollForm.Visibility = Visibility.Collapsed;
                ShowScan(FlowState.Idle, "Registro protegido", "Verifica tu acceso para iniciar la captura.");
                StartScreenWork(ReaderMonitorAsync);
            }
            else
            {
                TitleText.Text = "Registro de asistencia";
                Subtitle.Text = "Identifica tu huella y elige Entrada o Salida.";
                NavButton.Content = "Administración  →";
                AttendanceMessage.Text = "La hora oficial la asigna UProApp.";
                ResumeButton.Visibility = Visibility.Collapsed;
                StartScreenWork(AttendanceLoopAsync);
            }
        }
        finally
        {
            navigating = false;
            NavButton.IsEnabled = !closing;
        }
    }

    private async Task ReaderMonitorAsync(CancellationToken cancellation)
    {
        while (true)
        {
            var status = await Task.Run(SecuGenReader.Probe, cancellation);
            cancellation.ThrowIfCancellationRequested();
            PaintReader(status);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellation);
        }
    }

    private async Task AttendanceLoopAsync(CancellationToken cancellation)
    {
        ClearIdentity();
        PaintProgress(0);
        while (true)
        {
            var status = await Task.Run(SecuGenReader.Probe, cancellation);
            cancellation.ThrowIfCancellationRequested();
            PaintReader(status);
            if (status.Status != ReaderStatus.Ready || !biometrics.HasEnrollments)
            {
                ShowScan(status.Status == ReaderStatus.Ready ? FlowState.Idle : FlowState.DeviceUnavailable,
                    "Coloca tu dedo en el lector", status.Status == ReaderStatus.Ready
                        ? "Aún no hay huellas registradas en esta sesión."
                        : ReaderInstruction(status));
                await Task.Delay(TimeSpan.FromSeconds(3), cancellation);
                continue;
            }

            ShowScan(FlowState.Capturing, "Coloca tu dedo en el lector", "Mantén el dedo quieto durante la lectura.");
            try
            {
                var result = await workflow.IdentifyAsync(
                    () => PostToScreen(cancellation, () => ShowScan(FlowState.Matching, "Verificando huella", "Buscando una coincidencia local.")),
                    () => PostToScreen(cancellation, () => ShowScan(FlowState.QualityRejected,
                        "Ajusta el dedo", "La lectura fue rechazada. Mantén el dedo quieto.")),
                    cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (result.Identity is { } identity)
                {
                    identified = identity;
                    identifiedAt = Stopwatch.GetTimestamp();
                    IdentityText.Text = identity.Employee.Name;
                    FingerText.Text = FingerLabels.First(item => item.Finger == identity.Finger).Label;
                    EntryButton.IsEnabled = ExitButton.IsEnabled = true;
                    PaintProgress(Biometrics.SampleCount);
                    ShowScan(FlowState.Identified, "Huella identificada", "Elige Entrada o Salida para registrar el marcaje.");
                    await Task.Delay(TimeSpan.FromSeconds(20), cancellation);
                    ClearIdentity();
                    PaintProgress(0);
                    AttendanceMessage.Text = "La identificación venció. Vuelve a colocar el dedo.";
                }
                else
                {
                    ShowScan(FlowState.Failed, "No se identificó la huella",
                        result.Status == IdentificationStatus.Ambiguous
                            ? "Hay más de una coincidencia posible. Retira el dedo e inténtalo de nuevo."
                            : "Retira el dedo y prueba con un dedo registrado.");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
                }
            }
            catch (ReaderFailure failure)
            {
                PaintReader(failure.Reader);
                ShowScan(FlowState.DeviceUnavailable, "Lector no disponible", ReaderInstruction(failure.Reader));
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
            }
            catch (TimeoutException)
            {
                ShowScan(FlowState.Idle, "Coloca tu dedo en el lector", "No se obtuvo una huella válida.");
            }
        }
    }

    private async void Authorize(object sender, RoutedEventArgs e)
    {
        if (closing || navigating)
            return;

        AuthorizeButton.IsEnabled = false;
        NavButton.IsEnabled = false;
        try
        {
            var token = lifetime.Token;
            var session = await authorization.AuthenticateAsync(token);
            if (session is null)
            {
                AdminDetail.Text = laboratory
                    ? "Acceso denegado. El laboratorio requiere una sesión elevada de administrador de Windows."
                    : "Acceso pendiente. UProApp aún no tiene un proveedor autorizado para este equipo.";
                return;
            }

            enrollmentSession = session;
            Employees.ItemsSource = await employees.GetEmployeesAsync(session, token);
            token.ThrowIfCancellationRequested();
            AdminTitle.Text = "Datos del registro";
            AdminDetail.Text = laboratory ? "Sesión de prueba autorizada por Windows." : "Selecciona la estilista y el dedo.";
            AuthorizeButton.Visibility = Visibility.Collapsed;
            EnrollForm.Visibility = Visibility.Visible;
            ShowScan(FlowState.Idle, "Selecciona el dedo", "La huella avanzará con cada muestra aceptada.");
            RefreshEnrollmentForm();
        }
        catch (OperationCanceledException) when (closing)
        {
        }
        catch (UnauthorizedAccessException)
        {
            enrollmentSession?.Dispose();
            enrollmentSession = null;
            AdminDetail.Text = "No se pudo autorizar el acceso a la lista de estilistas.";
        }
        finally
        {
            AuthorizeButton.IsEnabled = NavButton.IsEnabled = !closing;
        }
    }

    private async void Enroll(object sender, RoutedEventArgs e)
    {
        if (closing || navigating || enrolling || enrollmentSession is not { IsActive: true } session
            || Employees.SelectedItem is not Employee employee || Fingers.SelectedItem is not FingerOption finger)
            return;

        enrolling = true;
        NavButton.IsEnabled = false;
        RefreshEnrollmentForm();
        await DrainScreenAsync();
        if (closing)
            return;
        NavButton.IsEnabled = true;
        StartScreenWork(token => RunEnrollmentAsync(employee, finger.Finger, session, token));
    }

    private async Task RunEnrollmentAsync(Employee employee, Finger finger, IEnrollmentSession session,
        CancellationToken cancellation)
    {
        PaintProgress(0);
        ShowScan(FlowState.Capturing, "Coloca el centro de tu dedo", "Apóyalo en el lector y mantenlo quieto.");
        try
        {
            var result = await workflow.EnrollAsync(employee, finger, session, windowSource!.Handle,
                count => PostToScreen(cancellation, () => PaintProgress(count)),
                inconsistent => PostToScreen(cancellation, () => ShowScan(
                    inconsistent ? FlowState.QualityRejected : FlowState.EnrollmentInProgress,
                    "Levanta el dedo y vuelve a colocarlo",
                    inconsistent ? "La muestra no coincide con las anteriores. Usa el mismo dedo."
                        : "Retíralo por completo y vuelve a apoyarlo suavemente.")),
                state => PostToScreen(cancellation, () =>
                {
                    State = state;
                    if (state == FlowState.QualityRejected)
                        ScanDetail.Text = "Muestra rechazada. Ajusta el dedo y mantenlo quieto.";
                    if (state == FlowState.Capturing)
                        ShowScan(state, "Vuelve a colocar el mismo dedo", "Puedes variar ligeramente la posición.");
                }), cancellation);

            if (result == EnrollmentStatus.Completed)
            {
                PaintProgress(Biometrics.SampleCount);
                ShowScan(FlowState.Success, "Huella registrada", $"{employee.Name} · disponible durante esta sesión.");
            }
            else
            {
                PaintProgress(0);
                ShowScan(FlowState.Failed, "No se registró la huella", result switch
                {
                    EnrollmentStatus.Duplicate => "El dedo o la huella ya está registrado en esta sesión.",
                    EnrollmentStatus.LimitReached => "La estilista ya tiene tres dedos registrados.",
                    _ => "El acceso administrativo ya no está autorizado."
                });
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            PaintProgress(0);
            ShowScan(FlowState.Idle, "Captura cancelada", "Puedes iniciar un registro nuevo.");
        }
        catch (ReaderFailure failure)
        {
            PaintProgress(0);
            PaintReader(failure.Reader);
            ShowScan(FlowState.DeviceUnavailable, "Lector no disponible", ReaderInstruction(failure.Reader));
        }
        catch (TimeoutException error)
        {
            PaintProgress(0);
            ShowScan(FlowState.Failed, "Captura interrumpida", error.Message);
        }
        finally
        {
            enrolling = false;
            RefreshEnrollmentForm();
            if (!closing && !navigating && !cancellation.IsCancellationRequested)
                StartScreenWork(ReaderMonitorAsync);
        }
    }

    private async void CancelEnrollment(object sender, RoutedEventArgs e)
    {
        if (!enrolling || navigating || closing)
            return;
        navigating = true;
        CancelButton.IsEnabled = false;
        try
        {
            await DrainScreenAsync();
            if (!closing)
                StartScreenWork(ReaderMonitorAsync);
        }
        finally
        {
            navigating = false;
            CancelButton.IsEnabled = true;
        }
    }

    private void RefreshEnrollmentForm()
    {
        if (EnrollButton is null)
            return;

        Employees.IsEnabled = Fingers.IsEnabled = !enrolling;
        CancelButton.Visibility = enrolling ? Visibility.Visible : Visibility.Collapsed;
        if (enrolling)
        {
            EnrollButton.IsEnabled = false;
            return;
        }

        var selection = Employees.SelectedItem as Employee;
        var registered = selection is null
            ? [] : gallery.Enrollments.Where(item => item.Identity.Employee.Id == selection.Id).ToArray();
        EnrollmentInventory.Text = selection is null ? "" : $"{registered.Length} de hasta 3 dedos registrados en esta sesión.";
        EnrollButton.IsEnabled = reader.Status == ReaderStatus.Ready && selection is not null
            && enrollmentSession is { IsActive: true } && registered.Length < 3
            && Fingers.SelectedItem is FingerOption finger && registered.All(item => item.Identity.Finger != finger.Finger);
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshEnrollmentForm();

    private async void MarkEntry(object sender, RoutedEventArgs e) => await MarkAsync(AttendanceAction.Entry);
    private async void MarkExit(object sender, RoutedEventArgs e) => await MarkAsync(AttendanceAction.Exit);

    private async Task MarkAsync(AttendanceAction action)
    {
        if (closing || navigating || submitting || identified is null
            || Stopwatch.GetElapsedTime(identifiedAt) > TimeSpan.FromSeconds(20))
            return;

        var request = new AttendanceEvent(Guid.NewGuid(), identified.Employee.Id, action, DateTimeOffset.Now);
        submitting = true;
        identified = null;
        EntryButton.IsEnabled = ExitButton.IsEnabled = NavButton.IsEnabled = false;
        try
        {
            await DrainScreenAsync();
            var result = await attendance.SubmitAsync(request, lifetime.Token);
            var name = action == AttendanceAction.Entry ? "Entrada" : "Salida";
            if (result is { Status: SyncStatus.Confirmed, OfficialTime: { } official })
            {
                ShowScan(FlowState.Success, $"{name} confirmada", "UProApp confirmó el marcaje.");
                AttendanceMessage.Text = $"Hora oficial · {MexicoTime(official)}";
            }
            else if (result.Status == SyncStatus.LocalPending)
            {
                ShowScan(FlowState.Success, $"{name} de prueba", "Marcaje local en memoria durante esta sesión.");
                AttendanceMessage.Text = $"Hora local provisional · {MexicoTime(request.ObservedOnDevice)}";
            }
            else if (result.Status == SyncStatus.OutcomeUnknown)
            {
                ShowScan(FlowState.Failed, "Resultado pendiente", "UProApp no entregó una confirmación completa.");
                AttendanceMessage.Text = $"Referencia · {request.Id:D}";
            }
            else
            {
                ShowScan(FlowState.Failed, "Marcaje no disponible", "La integración con UProApp está pendiente.");
                AttendanceMessage.Text = "Sin registro de asistencia.";
            }
        }
        catch (OperationCanceledException) when (closing)
        {
        }
        finally
        {
            submitting = false;
            NavButton.IsEnabled = !closing;
            ResumeButton.Visibility = Visibility.Visible;
            ResumeButton.IsEnabled = !closing;
        }
    }

    private void Resume(object sender, RoutedEventArgs e)
    {
        if (closing || navigating || submitting)
            return;
        ResumeButton.Visibility = Visibility.Collapsed;
        ResumeButton.IsEnabled = false;
        AttendanceMessage.Text = "La hora oficial la asigna UProApp.";
        StartScreenWork(AttendanceLoopAsync);
    }

    private static string MexicoTime(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, MexicoCity).ToString("HH:mm:ss", Spanish);

    private void ClearIdentity()
    {
        identified = null;
        IdentityText.Text = "Esperando una huella";
        FingerText.Text = "";
        EntryButton.IsEnabled = ExitButton.IsEnabled = false;
    }

    private void PaintProgress(int count)
    {
        Fingerprint.ShowAccepted(count, Biometrics.SampleCount);
        SampleBar.Value = count;
        SampleCount.Text = $"{count} de {Biometrics.SampleCount} muestras aceptadas";
    }

    private void PaintReader(ReaderState status)
    {
        reader = status;
        ReaderText.Text = status.Status switch
        {
            ReaderStatus.Ready => "Lector disponible",
            ReaderStatus.Absent => "Lector desconectado",
            ReaderStatus.WindowsError => "Lector con problema en Windows",
            ReaderStatus.SdkMissing => "Conectado · falta SDK",
            ReaderStatus.Busy => "Lector ocupado",
            _ => status.Code is { } code ? $"Error de SDK ({code})" : "Error de SDK"
        };
        ReaderDot.Fill = status.Status == ReaderStatus.Ready ? Brushes.SeaGreen : Brushes.SaddleBrown;
        if (administrative && !enrolling)
            RefreshEnrollmentForm();
    }

    private static string ReaderInstruction(ReaderState status) => status.Status switch
    {
        ReaderStatus.Absent => "Conecta el SecuGen U20 por USB.",
        ReaderStatus.WindowsError => "Revisa el lector y su controlador en Windows.",
        ReaderStatus.SdkMissing => "Windows detectó el lector. Falta el runtime SecuGen FDx.",
        ReaderStatus.Busy => "Cierra la otra aplicación que usa el lector.",
        _ => "Revisa la instalación y la arquitectura del runtime de SecuGen."
    };

    private void ShowScan(FlowState state, string title, string detail)
    {
        State = state;
        var changed = Prompt.Text != title;
        Prompt.Text = title;
        ScanDetail.Text = detail;
        if (changed)
            UIElementAutomationPeer.FromElement(Prompt)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void PostToScreen(CancellationToken cancellation, Action action) => Dispatcher.BeginInvoke(() =>
    {
        if (!cancellation.IsCancellationRequested)
            action();
    });

    private async void CloseAfterWork(object? sender, CancelEventArgs e)
    {
        if (allowClose)
            return;
        e.Cancel = true;
        if (closing)
            return;
        closing = true;
        lifetime.Cancel();
        await DrainScreenAsync();
        windowSource?.RemoveHook(ReadDeviceMessage);
        enrollmentSession?.Dispose();
        gallery.Dispose();
        lifetime.Dispose();
        allowClose = true;
        await Dispatcher.InvokeAsync(Close);
    }

    private sealed record FingerOption(Finger Finger, string Label);
}
