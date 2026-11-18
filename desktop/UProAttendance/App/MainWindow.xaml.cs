using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UProAttendance.Core;
using UProAttendance.Core.Editorial;
using UProAttendance.Hardware;
using UProAttendance.Presentation;

namespace UProAttendance;

public partial class MainWindow : Window
{
    private static readonly TimeZoneInfo MexicoCity = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");
    private static readonly (Finger Finger, string Label)[] FingerLabels =
    [
        (Finger.RightThumb, "Pulgar derecho"), (Finger.RightIndex, "Índice derecho"),
        (Finger.RightMiddle, "Medio derecho"), (Finger.RightRing, "Anular derecho"),
        (Finger.RightLittle, "Meñique derecho"), (Finger.LeftThumb, "Pulgar izquierdo"),
        (Finger.LeftIndex, "Índice izquierdo"), (Finger.LeftMiddle, "Medio izquierdo"),
        (Finger.LeftRing, "Anular izquierdo"), (Finger.LeftLittle, "Meñique izquierdo")
    ];

    private readonly ITemplateGallery gallery;
    private readonly OperationalTerminal? terminal;
    private readonly HttpClient? terminalHttp;
    private Task? terminalWork;
    private bool readerUnavailableReported;
    private readonly Biometrics biometrics;
    private readonly ReaderWorkflow workflow;
    private readonly IEnrollmentAuthorization authorization;
    private readonly IEmployeeProvider employees;
    private readonly DailyAttendance attendance;
    private readonly IAppointmentHoursProvider appointmentHours;
    private readonly bool laboratory;
    private readonly CancellationTokenSource lifetime = new();
    private readonly AttendanceGreetings greetings = new();
    private readonly EditorialMessages editorial;
    internal static readonly TimeSpan FeedbackDuration = TimeSpan.FromSeconds(30);
    private readonly DispatcherTimer feedbackTimer = new() { Interval = FeedbackDuration };
    private string? feedbackEmployeeId;
    private long feedbackSequence;
    private Task? screenWork;
    private CancellationTokenSource? screenCancellation;
    private IEnrollmentSession? enrollmentSession;
    private HwndSource? windowSource;
    private ReaderState reader = new(ReaderStatus.SdkMissing);
    private DateOnly? scheduleLoadedFor;
    private Screen screen;
    private bool waitingForLift;
    private bool navigating;
    private bool enrolling;
    private bool submitting;
    private bool closing;
    private bool allowClose;

    public MainWindow(bool laboratory) : this(laboratory, null, null, activateTerminal: true)
    {
    }

    internal MainWindow(bool laboratory, IEnrollmentAuthorization? authorizationOverride, IEmployeeProvider? employeesOverride,
        EditorialMessages? editorialOverride = null, bool activateTerminal = false)
    {
        this.laboratory = laboratory;
        if (activateTerminal && !laboratory)
        {
            terminalHttp = UproCheckApi.CreateHttpClient();
            try { terminal = new(TerminalFiles.OperationalDirectory(), new(terminalHttp)); }
            catch
            {
                terminalHttp.Dispose();
                throw;
            }
        }
        gallery = terminal?.Gallery ?? (ITemplateGallery)new SessionTemplateGallery();
        biometrics = new(gallery);
        workflow = new(biometrics);
        var integration = new UnconfiguredUproApp();
        appointmentHours = (IAppointmentHoursProvider?)terminal ?? integration;
#if DEBUG
        if (laboratory)
        {
            var debugIntegration = new Laboratory();
            authorization = debugIntegration;
            employees = debugIntegration;
            attendance = new(new(debugIntegration));
        }
        else
#endif
        {
            authorization = (IEnrollmentAuthorization?)terminal ?? integration;
            employees = (IEmployeeProvider?)terminal ?? integration;
            attendance = new(new(integration));
        }

        authorization = authorizationOverride ?? authorization;
        employees = employeesOverride ?? employees;
        editorial = editorialOverride ?? LocalEditorialHistory.Create();
        InitializeComponent();
        if (BuildStamp.Read(MexicoCity) is { } buildStamp)
        {
            DevelopmentBuildStamp.Text = buildStamp;
            DevelopmentBuildStamp.Visibility = Visibility.Visible;
        }
        feedbackTimer.Tick += (_, _) => ShowAttendanceReader(
            waitingForLift ? FlowState.WaitingForLift : FlowState.Capturing, keepFeedback: false);
        ShowPage(Screen.Attendance);
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
        var attendanceScreen = screen == Screen.Attendance;
        ScannerVisual.Height = attendanceScreen ? (compact ? 230 : 320) : (compact ? 132 : 198);
        ScannerVisual.Margin = new Thickness(0, 0, 0, compact ? 6 : 19);
        Prompt.FontSize = attendanceScreen ? (compact ? 24 : 30) : 24;
        AttendanceReceipt.SetCompactLayout(compact);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        windowSource = (HwndSource)PresentationSource.FromVisual(this);
        windowSource.AddHook(ReadDeviceMessage);
        StartScreenWork(AttendanceLoopAsync);
        if (terminal is not null)
        {
            void Update() => PostToScreen(lifetime.Token, PaintTerminal);
            terminal.AccessChanged += Update;
            var token = lifetime.Token;
            terminalWork = Task.Run(() => RunScreenWorkAsync(ct => terminal.RunAsync(Update, ct), token));
            PaintTerminal();
            if (terminal.Device?.DeviceToken is null)
                Dispatcher.BeginInvoke(() => OpenTerminalSetup(this, new RoutedEventArgs()));
        }
    }

    private void PaintTerminal()
    {
        if (terminal is null || closing)
            return;
        TerminalStatus.Text = terminal.Status;
        TerminalStatus.ToolTip = terminal.Incidents is { } incidents
            ? terminal.Status + Environment.NewLine + incidents : terminal.Status;
        TerminalSetupButton.Visibility = Visibility.Visible;
        TerminalSetupButton.Content = terminal.Device?.DeviceToken is null ? "Configurar terminal" : "Credencial";
        if (enrollmentSession is not { IsActive: true })
        {
            if (enrolling)
                screenCancellation?.Cancel();
            if (screen == Screen.Employee)
                RefreshEnrollmentForm();
        }
        if (screen == Screen.AdminHome && !navigating)
        {
            SettingsButton.IsEnabled = EmployeesButton.IsEnabled = terminal.CanAdminister;
            if (enrollmentSession is not { IsActive: true })
            {
                AdminHomeStatus.Text = terminal.CanAdminister ? "" : "Administración requiere autorización en línea.";
                AdminHomeStatus.Visibility = terminal.CanAdminister ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        if (screen == Screen.Settings)
            BreakToggle.IsEnabled = enrollmentSession is { IsActive: true };
    }

    private void OpenTerminalSetup(object sender, RoutedEventArgs e)
    {
        if (terminal is not null && !closing)
        {
            TerminalSetup.Show(this, terminal, lifetime.Token);
            PaintTerminal();
        }
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
            if (screen is Screen.Attendance or Screen.AdminHome or Screen.Capture)
                await DrainScreenAsync();
            if (closing)
                return;

            switch (screen)
            {
                case Screen.Attendance:
                    ShowPage(Screen.AdminHome);
                    SettingsButton.IsEnabled = EmployeesButton.IsEnabled = false;
                    AdminHomeStatus.Text = "Verificando acceso administrativo…";
                    AdminHomeStatus.Visibility = Visibility.Visible;
                    StartScreenWork(ReaderMonitorAsync);
                    await VerifyAdministrationAsync();
                    break;
                case Screen.AdminHome:
                    enrollmentSession?.Dispose();
                    enrollmentSession = null;
                    Employees.ItemsSource = null;
                    ShowPage(Screen.Attendance);
                    StartScreenWork(AttendanceLoopAsync);
                    break;
                case Screen.Settings:
                case Screen.Employee:
                    ShowPage(Screen.AdminHome);
                    break;
                case Screen.Capture:
                    Fingers.SelectedIndex = -1;
                    ShowPage(Screen.Employee);
                    RefreshEnrollmentForm();
                    StartScreenWork(ReaderMonitorAsync);
                    Fingers.Focus();
                    break;
            }
        }
        finally
        {
            navigating = false;
            NavButton.IsEnabled = !closing;
        }
    }

    private void ShowPage(Screen next)
    {
        feedbackTimer.Stop();
        ConfirmationLabel.Visibility = Visibility.Collapsed;
        AttendanceReceipt.Visibility = Visibility.Collapsed;
        screen = next;
        var attendanceScreen = next == Screen.Attendance;
        var showsScanner = next is Screen.Attendance or Screen.Capture;
        ContentRoot.VerticalAlignment = showsScanner ? VerticalAlignment.Center : VerticalAlignment.Top;
        PageIntro.Visibility = StatusBar.Visibility = attendanceScreen ? Visibility.Collapsed : Visibility.Visible;
        ScanPanel.Visibility = showsScanner ? Visibility.Visible : Visibility.Collapsed;
        CaptureAside.Visibility = next == Screen.Capture ? Visibility.Visible : Visibility.Collapsed;
        ScannerGap.Width = new GridLength(attendanceScreen ? 0 : 20);
        ScannerAside.Width = attendanceScreen ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ScannerCard.Background = attendanceScreen ? Brushes.Transparent : (Brush)FindResource("ScannerSurface");
        ScannerCard.BorderThickness = new Thickness(attendanceScreen ? 0 : 1);
        SetCompactLayout((ActualHeight > 0 ? ActualHeight : Height) < 620);
        AdminHomePanel.Visibility = next == Screen.AdminHome ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = next == Screen.Settings ? Visibility.Visible : Visibility.Collapsed;
        EnrollForm.Visibility = next == Screen.Employee ? Visibility.Visible : Visibility.Collapsed;
        CaptureSummary.Visibility = ProgressPanel.Visibility = next == Screen.Capture ? Visibility.Visible : Visibility.Collapsed;

        (TitleText.Text, Subtitle.Text, NavButton.Content) = next switch
        {
            Screen.Attendance => ("Registro de asistencia", "", "Administración  →"),
            Screen.AdminHome => ("Administración", "", "←  Registro de asistencia"),
            Screen.Settings => ("Configuración", "", "←  Administración"),
            Screen.Employee => ("Alta de empleado", "Selecciona una estilista existente para registrar sus huellas.", "←  Administración"),
            _ => ("Registrar huella", "", "←  Alta de empleado")
        };
        Subtitle.Visibility = string.IsNullOrEmpty(Subtitle.Text) ? Visibility.Collapsed : Visibility.Visible;
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
        waitingForLift = false;
        PaintProgress(0);
        await RefreshScheduleAsync(SalonDay(DateTimeOffset.Now), cancellation);
        while (true)
        {
            if (!biometrics.HasEnrollments)
            {
                var status = await Task.Run(SecuGenReader.Probe, cancellation);
                cancellation.ThrowIfCancellationRequested();
                PaintReader(status);
                await AuditReaderStatusAsync(status, cancellation);
                if (!feedbackTimer.IsEnabled)
                    ShowScan(status.Status == ReaderStatus.Ready ? FlowState.Idle : FlowState.DeviceUnavailable,
                        status.Status == ReaderStatus.Ready ? "Aún no hay huellas registradas" : "Lector no disponible",
                        status.Status == ReaderStatus.Ready ? "Registra una huella desde Administración."
                            : ReaderInstruction(status));
                await Task.Delay(TimeSpan.FromSeconds(3), cancellation);
                continue;
            }

            try
            {
                if (!feedbackTimer.IsEnabled)
                    ShowScan(FlowState.Idle, "Preparando el lector", "");
                await workflow.WatchAttendanceAsync(windowSource!.Handle,
                    state => PostToScreen(cancellation, () => ShowAttendanceReader(state)),
                    result => Dispatcher.InvokeAsync(() => RecordIdentificationAsync(result, cancellation)).Task.Unwrap(),
                    cancellation, () => AuditAsync("LECTURA_HUELLA", "RECHAZADO", null, "CAPTURA_FALLIDA", cancellation));
            }
            catch (ReaderFailure failure)
            {
                PaintReader(failure.Reader);
                await AuditReaderStatusAsync(failure.Reader, cancellation);
                if (!feedbackTimer.IsEnabled)
                    ShowScan(FlowState.DeviceUnavailable, "Lector no disponible", ReaderInstruction(failure.Reader));
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
            }
        }
    }

    private Task AuditReaderStatusAsync(ReaderState status, CancellationToken cancellation)
    {
        if (status.Status == ReaderStatus.Ready)
            readerUnavailableReported = false;
        else if (!readerUnavailableReported)
        {
            readerUnavailableReported = true;
            return AuditAsync("LECTURA_HUELLA", "ERROR", null, "LECTOR_NO_DISPONIBLE", cancellation);
        }
        return Task.CompletedTask;
    }

    private async Task AuditAsync(string type, string result, string? id, string? reason, CancellationToken cancellation)
    {
        if (terminal is null)
            return;
        try { await Task.Run(() => terminal.RecordAudit(type, result, id, reason), cancellation); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal void ShowAttendanceReader(FlowState state, bool keepFeedback = true)
    {
        if (keepFeedback && feedbackTimer.IsEnabled)
        {
            if (state is FlowState.Matching or FlowState.WaitingForLift)
                waitingForLift = true;
            else if (state == FlowState.Capturing)
                waitingForLift = false;
            return;
        }

        switch (state)
        {
            case FlowState.Capturing:
                readerUnavailableReported = false;
                waitingForLift = false;
                PaintReader(new(ReaderStatus.Ready));
                PaintProgress(0);
                ShowScan(state, "Coloca tu dedo en el lector", "");
                break;
            case FlowState.Matching:
                waitingForLift = true;
                ShowScan(state, "Verificando huella", "");
                break;
            case FlowState.QualityRejected:
                ShowScan(state, "Ajusta el dedo", "Apoya el centro de la huella y mantenlo quieto.");
                break;
            case FlowState.WaitingForLift:
                waitingForLift = true;
                PaintReader(new(ReaderStatus.Ready));
                PaintProgress(0);
                ShowScan(state, "Retira el dedo", "");
                break;
        }
    }

    private async Task VerifyAdministrationAsync()
    {
        try
        {
            var token = lifetime.Token;
            var session = await authorization.AuthenticateAsync(token);
            if (session is null)
            {
                AdminHomeStatus.Text = laboratory
                    ? "Acceso denegado. El laboratorio requiere una sesión elevada de administrador de Windows."
                    : terminal is null
                        ? "Acceso pendiente. Todavía no está configurado el proveedor de autorización de UProApp."
                        : "Administración requiere autorización en línea.";
                return;
            }

            if (!session.IsActive || token.IsCancellationRequested)
            {
                session.Dispose();
                token.ThrowIfCancellationRequested();
                throw new UnauthorizedAccessException();
            }

            enrollmentSession?.Dispose();
            enrollmentSession = session;
            AdminHomeStatus.Visibility = Visibility.Collapsed;
            SettingsButton.IsEnabled = EmployeesButton.IsEnabled = true;
            SettingsButton.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            // Never let an asynchronous authorization failure terminate the Windows UI.
            System.Diagnostics.Trace.TraceError("Administrative authorization failed: {0}", error.GetType().Name);
            enrollmentSession?.Dispose();
            enrollmentSession = null;
            AdminHomeStatus.Text = "No se pudo verificar el acceso administrativo.";
        }
    }

    private async void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (terminal is not null && enrollmentSession is not { IsActive: true })
            await AcquireOperationalSession();
        if (closing || navigating || screen != Screen.AdminHome || enrollmentSession is not { IsActive: true })
            return;

        var day = SalonDay(DateTimeOffset.Now);
        BreakToggle.IsEnabled = true;
        BreakToggle.IsChecked = terminal?.HasBreak(day) ?? attendance.HasBreak(day);
        PaintBreakSetting(day);
        ShowPage(Screen.Settings);
        BreakToggle.Focus();
    }

    private async void OpenEmployees(object sender, RoutedEventArgs e)
    {
        if (terminal is not null && enrollmentSession is not { IsActive: true })
            await AcquireOperationalSession();
        if (closing || navigating || screen != Screen.AdminHome || enrollmentSession is not { IsActive: true } session)
            return;

        navigating = true;
        NavButton.IsEnabled = SettingsButton.IsEnabled = EmployeesButton.IsEnabled = false;
        AdminHomeStatus.Text = "Cargando estilistas…";
        AdminHomeStatus.Visibility = Visibility.Visible;
        try
        {
            var token = lifetime.Token;
            var roster = await employees.GetEmployeesAsync(session, token);
            token.ThrowIfCancellationRequested();
            if (!session.IsActive)
                throw new UnauthorizedAccessException();

            var selectedId = (Employees.SelectedItem as Employee)?.Id;
            Employees.ItemsSource = roster;
            Employees.SelectedItem = roster.FirstOrDefault(employee => employee.Id == selectedId);
            EmployeesEmpty.Visibility = roster.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AdminHomeStatus.Visibility = Visibility.Collapsed;
            ShowPage(Screen.Employee);
            RefreshEnrollmentForm();
            Employees.Focus();
        }
        catch (OperationCanceledException) when (closing)
        {
        }
        catch (Exception error) when (error is UnauthorizedAccessException or HttpRequestException or TimeoutException)
        {
            AdminHomeStatus.Text = "No se pudo cargar la lista de estilistas. Vuelve a intentarlo.";
        }
        finally
        {
            navigating = false;
            NavButton.IsEnabled = !closing;
            SettingsButton.IsEnabled = EmployeesButton.IsEnabled = !closing && enrollmentSession is { IsActive: true };
        }
    }

    private async Task AcquireOperationalSession()
    {
        if (closing || navigating || terminal is null)
            return;
        enrollmentSession?.Dispose();
        enrollmentSession = await terminal.AuthenticateAsync(lifetime.Token);
    }

    private async void Enroll(object sender, RoutedEventArgs e)
    {
        if (closing || navigating || enrolling || screen != Screen.Employee || !EnrollButton.IsEnabled
            || enrollmentSession is not { IsActive: true } session
            || Employees.SelectedItem is not Employee employee || Fingers.SelectedItem is not FingerOption finger)
            return;

        enrolling = true;
        navigating = true;
        NavButton.IsEnabled = false;
        RefreshEnrollmentForm();
        await DrainScreenAsync();
        navigating = false;
        if (closing)
            return;
        CaptureEmployee.Text = employee.Name;
        CaptureFinger.Text = finger.Label;
        CaptureBackButton.Content = "Cancelar captura";
        ShowPage(Screen.Capture);
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
                ShowScan(FlowState.Success, "Huella registrada", terminal is null
                    ? $"{employee.Name} · disponible durante esta sesión." : employee.Name);
            }
            else
            {
                await AuditAsync("ALTA_HUELLA", "RECHAZADO", employee.Id,
                    result == EnrollmentStatus.Denied ? "CANCELADO" : "CAPTURA_FALLIDA", lifetime.Token);
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
            if (!closing)
                await AuditAsync("ALTA_HUELLA", "RECHAZADO", employee.Id, "CANCELADO", lifetime.Token);
            PaintProgress(0);
            ShowScan(FlowState.Idle, "Captura cancelada", "Puedes iniciar un registro nuevo.");
        }
        catch (ReaderFailure failure)
        {
            await AuditAsync("ALTA_HUELLA", "ERROR", employee.Id, "CAPTURA_FALLIDA", lifetime.Token);
            PaintProgress(0);
            PaintReader(failure.Reader);
            ShowScan(FlowState.DeviceUnavailable, "Lector no disponible", ReaderInstruction(failure.Reader));
        }
        catch (TimeoutException error)
        {
            await AuditAsync("ALTA_HUELLA", "ERROR", employee.Id, "CAPTURA_FALLIDA", lifetime.Token);
            PaintProgress(0);
            ShowScan(FlowState.Failed, "Captura interrumpida", error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await AuditAsync("ALTA_HUELLA", "ERROR", employee.Id, "FALLO_LOCAL", lifetime.Token);
            PaintProgress(0);
            ShowScan(FlowState.Failed, "No se guardó la huella", "Revisa el almacenamiento de la terminal.");
        }
        finally
        {
            enrolling = false;
            CaptureBackButton.Content = State == FlowState.Success ? "Registrar otro dedo" : "Volver a elegir dedo";
            if (!closing && !navigating && !cancellation.IsCancellationRequested)
                StartScreenWork(ReaderMonitorAsync);
        }
    }

    private void RefreshEnrollmentForm()
    {
        if (EnrollButton is null)
            return;

        Employees.IsEnabled = Fingers.IsEnabled = !enrolling
            && (terminal is null || enrollmentSession is { IsActive: true });
        if (enrolling)
        {
            EnrollButton.IsEnabled = false;
            return;
        }

        var selection = Employees.SelectedItem as Employee;
        FingerFields.Visibility = selection is null ? Visibility.Collapsed : Visibility.Visible;
        var registered = selection is null
            ? [] : gallery.Enrollments.Where(item => item.Identity.Employee.Id == selection.Id).ToArray();
        EnrollmentInventory.Text = selection is null ? "" : $"{registered.Length} de hasta 3 dedos registrados.";
        EnrollButton.IsEnabled = reader.Status == ReaderStatus.Ready && selection is not null
            && enrollmentSession is { IsActive: true } && registered.Length < 3
            && Fingers.SelectedItem is FingerOption finger && registered.All(item => item.Identity.Finger != finger.Finger);
    }

    private void EmployeeChanged(object sender, SelectionChangedEventArgs e)
    {
        Fingers.SelectedIndex = -1;
        RefreshEnrollmentForm();
    }

    private void FingerChanged(object sender, SelectionChangedEventArgs e) => RefreshEnrollmentForm();

    private async void ChangeBreak(object sender, RoutedEventArgs e)
    {
        if (screen == Screen.Settings && enrollmentSession is { IsActive: true })
        {
            var day = SalonDay(DateTimeOffset.Now);
            if (terminal is null)
                attendance.SetBreak(day, BreakToggle.IsChecked == true);
            else
            {
                var enabled = BreakToggle.IsChecked == true;
                BreakToggle.IsEnabled = false;
                try { await Task.Run(() => terminal.SetBreakAsync(day, enabled, lifetime.Token), lifetime.Token); }
                catch (OperationCanceledException) when (closing) { return; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    BreakToggle.IsChecked = terminal.HasBreak(day);
                }
                PaintTerminal();
            }
            PaintBreakSetting(day);
        }
    }

    private async Task RefreshScheduleAsync(DateOnly day, CancellationToken cancellation)
    {
        if (scheduleLoadedFor != day)
        {
            var total = await appointmentHours.GetTotalAsync(day, cancellation);
            cancellation.ThrowIfCancellationRequested();
            attendance.ApplyAppointmentHours(day, total);
            scheduleLoadedFor = day;
        }
    }

    private void PaintBreakSetting(DateOnly day)
    {
        BreakDescription.Text = (terminal?.HasBreak(day) ?? attendance.HasBreak(day))
            ? "Incluye inicio y fin de descanso."
            : "Solo entrada y salida.";
    }

    internal async Task RecordIdentificationAsync(IdentificationResult identification, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (identification.Identity is not { } identity)
        {
            await AuditAsync("LECTURA_HUELLA", "RECHAZADO", null, "NO_RECONOCIDA", cancellation);
            if (feedbackTimer.IsEnabled)
                return;
            ShowScan(FlowState.Failed, "No se identificó la huella",
                identification.Status == IdentificationStatus.Ambiguous
                    ? "Retira el dedo y vuelve a colocarlo para verificar la coincidencia."
                    : "Retira el dedo y prueba con uno registrado.");
            return;
        }

        // Prevent an accidental second punch while the same employee's greeting is visible.
        if (IsFeedbackForEmployee(identity.Employee.Id))
        {
            if (terminal?.FindEmployee(identity.Employee.Id) is { } repeated)
                await AuditAsync("LECTURA_HUELLA", "EXITOSO", repeated.Id, null, cancellation);
            return;
        }

        var observedAt = DateTimeOffset.Now;
        var day = SalonDay(observedAt);
        if (feedbackTimer.IsEnabled && terminal?.FindEmployee(identity.Employee.Id) is not null)
            ShowScan(FlowState.Matching, "Verificando huella", "");
        submitting = true;
        NavButton.IsEnabled = false;
        try
        {
            await RefreshScheduleAsync(day, cancellation);
            var currentEmployee = terminal is null ? identity.Employee : terminal.FindEmployee(identity.Employee.Id);
            if (currentEmployee is null)
            {
                await AuditAsync("LECTURA_HUELLA", "EXITOSO", identity.Employee.Id, null, cancellation);
                ShowScan(FlowState.Failed, "Empleado no disponible", "Retira el dedo.");
                return;
            }
            var punch = terminal is null
                ? await attendance.PunchAsync(day, identity.Employee.Id, observedAt, cancellation)
                : await Task.Run(() => terminal.PunchAsync(identity.Employee.Id, observedAt, cancellation), cancellation);
            if (terminal is not null && punch.Result?.Status == SyncStatus.Unavailable)
            {
                ShowScan(FlowState.Failed, "Marcaje no disponible", "Retira el dedo. Revisa el estado de la terminal.");
                return;
            }
            if (punch.Event is not { } request || punch.Result is not { } result)
            {
                ShowScan(FlowState.Identified, $"{currentEmployee.GreetingName}, tu salida de hoy ya está registrada.", "Retira el dedo.");
                return;
            }

            if (result.Status is SyncStatus.Confirmed or SyncStatus.LocalPending)
            {
                if (punch.Provisional)
                    TerminalStatus.Text = "Jornada provisional · pendiente de verificación";
                _ = PresentAttendanceAsync(request, terminal?.FindEmployee(identity.Employee.Id) ?? currentEmployee, result);
            }
            else if (result.Status == SyncStatus.OutcomeUnknown)
            {
                ShowScan(FlowState.Failed, "Marcaje pendiente", "Retira el dedo. La próxima lectura reintentará este marcaje.");
            }
            else
            {
                ShowScan(FlowState.Failed, "Marcaje no disponible", "Retira el dedo. Revisa el estado de la terminal.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowScan(FlowState.Failed, "No se pudo guardar el marcaje", "Revisa el almacenamiento de la terminal.");
        }
        finally
        {
            submitting = false;
            NavButton.IsEnabled = !closing;
        }
    }

    internal bool IsFeedbackForEmployee(string employeeId) =>
        feedbackTimer.IsEnabled && feedbackEmployeeId == employeeId;

    internal async Task PresentAttendanceAsync(AttendanceEvent attendanceEvent, Employee employee, AttendanceResult result)
    {
        if (screen != Screen.Attendance
            || result is not ({ Status: SyncStatus.LocalPending } or { Status: SyncStatus.Confirmed, OfficialTime: not null }))
            return;

        var action = attendanceEvent.Action;
        var local = result.Status == SyncStatus.LocalPending;
        var test = local && !result.Operational;
        var sequence = ++feedbackSequence;
        feedbackEmployeeId = employee.Id;
        if (action is AttendanceAction.Entry or AttendanceAction.Exit)
        {
            var instant = local ? attendanceEvent.ObservedOnDevice : result.OfficialTime!.Value;
            var time = TimeZoneInfo.ConvertTime(instant, MexicoCity).ToString("HH:mm", CultureInfo.InvariantCulture);
            var receipt = test ? $"{ActionName(action)} de prueba · {time}" : $"{ActionName(action)} registrada · {time}";
            ShowScan(FlowState.Success, "", "");
            ScanPanel.Visibility = Visibility.Collapsed;
            AttendanceReceipt.Visibility = Visibility.Visible;
            AttendanceReceipt.Present(action == AttendanceAction.Exit, receipt,
                AttendanceGreetings.ConfirmationSalutation(action, employee));
        }
        else
        {
            PaintProgress(Biometrics.SampleCount);
            ShowScan(FlowState.Success, greetings.NextBreak(action, employee.GreetingName), "");
            ConfirmationLabel.Text = test ? $"{ActionName(action)} · Prueba" : ActionName(action);
            ConfirmationLabel.Visibility = Visibility.Visible;
        }
        feedbackTimer.Start();

        if (action is AttendanceAction.Entry or AttendanceAction.Exit)
        {
            // The accepted mark is already visible. I/O never delays the reader or the confirmation.
            var message = await Task.Run(() => editorial.Select(attendanceEvent, employee, result));
            if (message is not null && sequence == feedbackSequence && feedbackTimer.IsEnabled)
                AttendanceReceipt.SetEditorialMessage(message, action == AttendanceAction.Entry ? "Diseñamos presencia." : null);
        }
    }

    private static string ActionName(AttendanceAction action) => action switch
    {
        AttendanceAction.Entry => "Entrada",
        AttendanceAction.BreakStart => "Inicio de descanso",
        AttendanceAction.BreakEnd => "Fin de descanso",
        _ => "Salida"
    };

    private static DateOnly SalonDay(DateTimeOffset instant) => TerminalClock.Day(instant);

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
        if (screen == Screen.Employee && !enrolling)
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
        feedbackTimer.Stop();
        ConfirmationLabel.Visibility = Visibility.Collapsed;
        if (AttendanceReceipt.Visibility == Visibility.Visible)
        {
            AttendanceReceipt.Visibility = Visibility.Collapsed;
            ScanPanel.Visibility = Visibility.Visible;
        }
        State = state;
        var changed = Prompt.Text != title;
        Prompt.Text = title;
        ScanDetail.Text = detail;
        ScanDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        if (changed && !string.IsNullOrEmpty(title))
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
        feedbackTimer.Stop();
        lifetime.Cancel();
        await DrainScreenAsync();
        if (terminalWork is not null)
            await terminalWork;
        windowSource?.RemoveHook(ReadDeviceMessage);
        enrollmentSession?.Dispose();
        if (terminal is null)
            (gallery as IDisposable)?.Dispose();
        terminal?.Dispose();
        terminalHttp?.Dispose();
        lifetime.Dispose();
        allowClose = true;
        await Dispatcher.InvokeAsync(Close);
    }

    private sealed record FingerOption(Finger Finger, string Label);
    private enum Screen { Attendance, AdminHome, Settings, Employee, Capture }
}
