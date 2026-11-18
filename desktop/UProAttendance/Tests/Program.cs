using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UProAttendance;
using UProAttendance.Core;
using UProAttendance.Core.Editorial;
using UProAttendance.Hardware;
using UProAttendance.Presentation;
using UProAttendance.Tests;

internal static class Program
{
    private static readonly IEnrollmentSession Authorized = new TestSession();

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--probe"])
                return Probe();
            if (args is ["--capture"])
                return CaptureOnDemand();
            if (args is ["--fixture", var rawSample])
            {
                ExtractAndMatchFixture(rawSample);
                return 0;
            }
            if (args is ["--screenshots", var destination])
            {
                Screenshots(destination);
                return 0;
            }

            (string Name, Action Test)[] tests =
            [
                ("Only accepted samples advance and three complete enrollment", EnrollmentProgress),
                ("Duplicate fingerprint cannot enroll against another employee or finger", DuplicateFinger),
                ("Authorization is required on each sample", RevokedAuthorization),
                ("Cancellation clears samples and avoids committing a draft", CancelledEnrollment),
                ("A fourth finger is rejected", FourthFinger),
                ("No match and a moderate-only match are rejected", InsufficientMatch),
                ("Another moderate candidate makes a strong identification ambiguous", Ambiguity),
                ("Several samples of the same enrolled finger are one candidate", SameFingerSamples),
                ("Unanswered attendance is pending under the same ID until official confirmation", AttendanceReconciliation),
                ("Unavailable and local-only submissions cannot acquire an official time", AttendanceLocal),
                ("Each employee follows their own automatic four-step sequence", DailyWithBreak),
                ("A new salon day starts with entry and no manual break carried over", DailyWithoutBreak),
                ("Appointment threshold is strict and manual settings are respected", AppointmentThreshold),
                ("Turning off breaks preserves a break already started", BreakAlreadyStarted),
                ("A pending automatic punch is retried with its original ID", DailyRetry),
                ("Confirmation salutations use first names and break greetings are preserved", GreetingCatalog),
                .. EditorialTests.All,
                .. TerminalTests.All
            ];

            foreach (var (name, test) in tests)
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void EnrollmentProgress()
    {
        using var gallery = new SessionTemplateGallery();
        var service = new Biometrics(gallery);
        using var draft = service.Begin(new("1", "Prueba"), Finger.RightIndex, Authorized);
        Equal(EnrollmentStatus.Accepted, Accept(draft, 1));
        var rejectedBytes = new byte[] { 2 };
        Equal(EnrollmentStatus.Inconsistent, draft.AcceptAsync(new(rejectedBytes), new Matcher(), default).Result);
        Equal((byte)0, rejectedBytes[0]);
        Equal(1, draft.AcceptedCount);
        Equal(0, gallery.Enrollments.Count);
        Equal(EnrollmentStatus.Accepted, Accept(draft, 1));
        Equal(EnrollmentStatus.Completed, Accept(draft, 1));
        Equal(3, gallery.Enrollments.Single().Samples.Count);
    }

    private static void DuplicateFinger()
    {
        using var gallery = new SessionTemplateGallery();
        gallery.Add(Enrollment("1", Finger.RightIndex, 1));
        var service = new Biometrics(gallery);
        using var differentEmployee = service.Begin(new("2", "Prueba"), Finger.LeftIndex, Authorized);
        Equal(EnrollmentStatus.Duplicate, Accept(differentEmployee, 1));
        using var differentLabel = service.Begin(new("1", "Prueba"), Finger.LeftIndex, Authorized);
        Equal(EnrollmentStatus.Duplicate, Accept(differentLabel, 1));
        using var sameSlot = service.Begin(new("1", "Prueba"), Finger.RightIndex, Authorized);
        Equal(EnrollmentStatus.Duplicate, Accept(sameSlot, 3));
        Equal(1, gallery.Enrollments.Count);
    }

    private static void RevokedAuthorization()
    {
        using var gallery = new SessionTemplateGallery();
        using var session = new TestSession();
        using var draft = new Biometrics(gallery).Begin(new("1", "Prueba"), Finger.RightIndex, session);
        Equal(EnrollmentStatus.Accepted, Accept(draft, 1));
        session.IsActive = false;
        Equal(EnrollmentStatus.Denied, Accept(draft, 1));
        Equal(1, draft.AcceptedCount);
        Equal(0, gallery.Enrollments.Count);
    }

    private static void CancelledEnrollment()
    {
        using var gallery = new SessionTemplateGallery();
        using var source = new CancellationTokenSource();
        var acceptedBytes = new byte[] { 1 };
        var cancelledBytes = new byte[] { 1 };
        using (var draft = new Biometrics(gallery).Begin(new("1", "Prueba"), Finger.RightIndex, Authorized))
        {
            Equal(EnrollmentStatus.Accepted, draft.AcceptAsync(new(acceptedBytes), new Matcher(), default).Result);
            source.Cancel();
            try
            {
                draft.AcceptAsync(new(cancelledBytes), new Matcher(), source.Token).GetAwaiter().GetResult();
                throw new InvalidOperationException("Expected cancellation.");
            }
            catch (OperationCanceledException)
            {
                Equal((byte)0, cancelledBytes[0]);
            }
        }

        Equal((byte)0, acceptedBytes[0]);
        Equal(0, gallery.Enrollments.Count);
    }

    private static void FourthFinger()
    {
        using var gallery = new SessionTemplateGallery();
        gallery.Add(Enrollment("1", Finger.RightThumb, 1));
        gallery.Add(Enrollment("1", Finger.RightIndex, 2));
        gallery.Add(Enrollment("1", Finger.RightMiddle, 3));
        using var draft = new Biometrics(gallery).Begin(new("1", "Prueba"), Finger.LeftIndex, Authorized);
        Equal(EnrollmentStatus.LimitReached, Accept(draft, 4));
    }

    private static void InsufficientMatch()
    {
        using var gallery = new SessionTemplateGallery();
        gallery.Add(Enrollment("1", Finger.RightIndex, 1));
        var service = new Biometrics(gallery);
        using var sample = Template(4);
        Equal(IdentificationStatus.NoMatch, service.Identify(sample, new Matcher(), default).Status);
        Equal(IdentificationStatus.NoMatch, service.Identify(sample, new Matcher(allowModerate: true), default).Status);
    }

    private static void Ambiguity()
    {
        using var gallery = new SessionTemplateGallery();
        gallery.Add(Enrollment("1", Finger.RightIndex, 1));
        gallery.Add(Enrollment("1", Finger.LeftThumb, 2));
        using var probe = Template(1);
        var result = new Biometrics(gallery).Identify(probe, new Matcher(allowModerate: true), default);
        Equal(IdentificationStatus.Ambiguous, result.Status);
        Equal<Identification?>(null, result.Identity);
    }

    private static void SameFingerSamples()
    {
        using var gallery = new SessionTemplateGallery();
        gallery.Add(Enrollment("1", Finger.RightIndex, 1, 1, 1));
        using var probe = Template(1);
        var result = new Biometrics(gallery).Identify(probe, new Matcher(), default);
        Equal(IdentificationStatus.Accepted, result.Status);
        Equal(Finger.RightIndex, result.Identity!.Finger);
    }

    private static void AttendanceReconciliation()
    {
        var request = new AttendanceEvent(Guid.NewGuid(), "1", AttendanceAction.Entry, DateTimeOffset.Now);
        var serverTime = DateTimeOffset.UtcNow.AddMinutes(1);
        var gateway = new TestGateway(null, new(SyncStatus.Unavailable), new(SyncStatus.Confirmed),
            new(SyncStatus.Confirmed, serverTime));
        var submission = new AttendanceSubmission(gateway);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Equal(SyncStatus.OutcomeUnknown, submission.SubmitAsync(request, default).Result.Status);
            Equal(request, submission.Pending.Single());
        }
        Equal(serverTime, submission.SubmitAsync(request, default).Result.OfficialTime);
        Equal(0, submission.Pending.Count);
        Equal(true, gateway.Requests.All(item => item == request));
    }

    private static void AttendanceLocal()
    {
        var request = new AttendanceEvent(Guid.NewGuid(), "1", AttendanceAction.Exit, DateTimeOffset.Now);
        var submission = new AttendanceSubmission(new TestGateway(new(SyncStatus.Unavailable), new(SyncStatus.LocalPending)));
        Equal(SyncStatus.Unavailable, submission.SubmitAsync(request, default).Result.Status);
        Equal(0, submission.Pending.Count);
        var local = submission.SubmitAsync(request, default).Result;
        Equal(SyncStatus.LocalPending, local.Status);
        Equal<DateTimeOffset?>(null, local.OfficialTime);
        Equal(request, submission.Pending.Single());
    }

    private static readonly DateOnly TestDay = new(2026, 11, 16);

    private static void DailyWithBreak()
    {
        var gateway = LocalGateway(5);
        var daily = new DailyAttendance(new(gateway));
        daily.SetBreak(TestDay, true);
        Equal(AttendanceAction.Entry, Punch(daily, "A").Event!.Action);
        Equal(AttendanceAction.Entry, Punch(daily, "B").Event!.Action);
        Equal(AttendanceAction.BreakStart, Punch(daily, "A").Event!.Action);
        Equal(AttendanceAction.BreakEnd, Punch(daily, "A").Event!.Action);
        var exit = Punch(daily, "A");
        Equal(AttendanceAction.Exit, exit.Event!.Action);
        Equal<AttendanceAction?>(null, exit.Next);
        Equal<AttendanceEvent?>(null, Punch(daily, "A").Event);
        Equal(5, gateway.Requests.Count);
    }

    private static void DailyWithoutBreak()
    {
        var daily = new DailyAttendance(new(LocalGateway(3)));
        daily.SetBreak(TestDay.AddDays(-1), true);
        Equal(AttendanceAction.Entry, Punch(daily, "A").Event!.Action);
        Equal(AttendanceAction.Exit, Punch(daily, "A").Event!.Action);
        Equal(AttendanceAction.Entry, Punch(daily, "A", TestDay.AddDays(1)).Event!.Action);
    }

    private static void AppointmentThreshold()
    {
        var daily = new DailyAttendance(new(LocalGateway(0)));
        daily.ApplyAppointmentHours(TestDay, TimeSpan.FromHours(6));
        Equal(false, daily.HasBreak(TestDay));
        daily.ApplyAppointmentHours(TestDay, TimeSpan.FromHours(6).Add(TimeSpan.FromMinutes(1)));
        Equal(true, daily.HasBreak(TestDay));
        daily.ApplyAppointmentHours(TestDay, null);
        Equal(true, daily.HasBreak(TestDay));
        daily.SetBreak(TestDay, false);
        daily.ApplyAppointmentHours(TestDay, TimeSpan.FromHours(7));
        Equal(false, daily.HasBreak(TestDay));
    }

    private static void BreakAlreadyStarted()
    {
        var daily = new DailyAttendance(new(LocalGateway(4)));
        Equal(AttendanceAction.Entry, Punch(daily, "A").Event!.Action);
        daily.SetBreak(TestDay, true);
        Equal(AttendanceAction.BreakStart, Punch(daily, "A").Event!.Action);
        daily.SetBreak(TestDay, false);
        Equal(AttendanceAction.BreakEnd, Punch(daily, "A").Event!.Action);
        Equal(AttendanceAction.Exit, Punch(daily, "A").Event!.Action);
    }

    private static void DailyRetry()
    {
        var gateway = new TestGateway(new(SyncStatus.Unavailable), null, new(SyncStatus.LocalPending));
        var daily = new DailyAttendance(new(gateway));
        Equal(AttendanceAction.Entry, Punch(daily, "A").Next);
        var pending = Punch(daily, "A");
        Equal(SyncStatus.OutcomeUnknown, pending.Result!.Status);
        var retry = Punch(daily, "A");
        Equal(pending.Event, retry.Event);
        Equal(AttendanceAction.Exit, retry.Next);
    }

    private static TestGateway LocalGateway(int count) =>
        new(Enumerable.Repeat<AttendanceResult?>(new(SyncStatus.LocalPending), count).ToArray());

    private static void GreetingCatalog()
    {
        var greetings = new AttendanceGreetings();
        foreach (var action in new[] { AttendanceAction.BreakStart, AttendanceAction.BreakEnd })
        {
            var templates = AttendanceGreetings.BreakTemplates(action);
            Equal(20, templates.Distinct(StringComparer.Ordinal).Count());
            string? previous = null;
            for (var i = 0; i < 30; i++)
            {
                var greeting = greetings.NextBreak(action, "María");
                Equal(true, greeting.Contains("María", StringComparison.Ordinal));
                Equal(false, greeting == previous);
                previous = greeting;
            }
        }

        foreach (var action in new[] { AttendanceAction.Entry, AttendanceAction.Exit })
        {
            var greeting = AttendanceGreetings.ConfirmationSalutation(action, "  María\tGarcía  ");
            Equal(true, greeting.Contains("María", StringComparison.Ordinal));
            Equal(false, greeting.Contains("García", StringComparison.Ordinal));
        }
    }

    private static DailyPunch Punch(DailyAttendance daily, string employee, DateOnly? day = null) =>
        daily.PunchAsync(day ?? TestDay, employee, DateTimeOffset.Now, default).GetAwaiter().GetResult();

    private sealed class TestGateway(params AttendanceResult?[] results) : IAttendanceGateway
    {
        public List<AttendanceEvent> Requests { get; } = [];

        public Task<AttendanceResult> SubmitAsync(AttendanceEvent attendance, CancellationToken cancellation)
        {
            var response = results[Requests.Count];
            Requests.Add(attendance);
            return response is null
                ? Task.FromException<AttendanceResult>(new TimeoutException())
                : Task.FromResult(response);
        }
    }

    private static int Probe()
    {
        var result = SecuGenReader.Probe();
        Console.WriteLine($"FDx. Process x64={Environment.Is64BitProcess}, status={result.Status}, operation={result.Operation}, SDK code={result.Code}");
        if (result.Status != ReaderStatus.Ready)
            return 2;
        using (var reader = SecuGenReader.Open())
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                using var unused = reader.Capture(cancellation.Token).Template;
                throw new InvalidOperationException("Expected cancellation before capture.");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("PASS Cancellation before capture");
            }
        }
        Equal(ReaderStatus.Ready, SecuGenReader.Probe().Status);
        Console.WriteLine("PASS Reader reopens after cancellation and disposal");
        return 0;
    }

    private static int CaptureOnDemand()
    {
        Console.WriteLine("Coloca un dedo en el lector cuando estés listo y pulsa Intro. No se guardarán datos biométricos.");
        if (Console.ReadLine() is null)
            return 2;
        using var reader = SecuGenReader.Open();
        var result = reader.Capture(default);
        using var sample = result.Template;
        Console.WriteLine($"Resultado de captura: {result.Status}");
        return result.Status == CaptureStatus.Accepted ? 0 : 2;
    }

    private static void ExtractAndMatchFixture(string path)
    {
        const int sampleWidth = 258;
        const int sampleHeight = 336;
        const int readerWidth = 300;
        const int readerHeight = 400;
        using var reader = SecuGenReader.Open();
        var input = File.ReadAllBytes(path);
        var padded = new byte[readerWidth * readerHeight];
        try
        {
            Equal(sampleWidth * sampleHeight, input.Length);
            Array.Fill(padded, byte.MaxValue);
            for (var row = 0; row < sampleHeight; row++)
                input.AsSpan(row * sampleWidth, sampleWidth)
                    .CopyTo(padded.AsSpan((row + (readerHeight - sampleHeight) / 2) * readerWidth
                        + (readerWidth - sampleWidth) / 2, sampleWidth));

            var capture1 = reader.ExtractTemplate(padded, default);
            using var first = capture1.Template;
            Equal(CaptureStatus.Accepted, capture1.Status);
            var capture2 = reader.ExtractTemplate(padded, default);
            using var second = capture2.Template;
            Equal(CaptureStatus.Accepted, capture2.Status);
            Equal(true, reader.Matches(first!, second!, MatchLevel.High));
            Console.WriteLine("PASS FDx extraction and SL_HIGH match using SecuGen's sample file");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(padded);
        }
    }

    private static void Screenshots(string destination)
    {
        Directory.CreateDirectory(destination);
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        using var store = new TemporaryEditorialStore();
        var catalog = MessageCatalog.LoadBundled();
        var show = new MainWindow(false, null, null, store.Service(catalog));
        show.Show();
        PumpUntil(() => ((TextBlock)show.FindName("ReaderText")).Text != "Comprobando…");
        Equal("Registro de asistencia", ((TextBlock)show.FindName("TitleText")).Text);
        show.ShowAttendanceReader(FlowState.Capturing);
        var prompt = (TextBlock)show.FindName("Prompt");
        var confirmation = (AttendanceConfirmation)show.FindName("AttendanceReceipt");
        var receipt = (TextBlock)confirmation.FindName("Receipt");
        var salutation = (TextBlock)confirmation.FindName("Salutation");
        Equal("Coloca tu dedo en el lector", prompt.Text);
        Equal(false, ((Border)show.FindName("StatusBar")).IsVisible);
        Equal(false, ((Border)show.FindName("CaptureAside")).IsVisible);
        Equal(false, ((TextBlock)show.FindName("ScanDetail")).IsVisible);
        var buildStamp = AssertBuildStamp(show);
        Console.WriteLine(buildStamp is null ? "PASS Development build label hidden" : $"PASS Development build label {buildStamp}");
        Snapshot(show, Path.Combine(destination, "registro-de-asistencia.png"), 1);
        var danna = new Employee("ui-danna", "Danna Ruiz") { Position = EmployeePosition.Stylist };
        var sandy = new Employee("ui-sandy", "Sandy Morales") { Position = EmployeePosition.Receptionist };
        var longName = new Employee("ui-long", "Mariana-Alejandra-del-Carmen Martínez");
        var observedEntry = new DateTimeOffset(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);
        var observedExit = observedEntry.AddHours(9);
        var local = new AttendanceResult(SyncStatus.LocalPending);
        var officialExit = new AttendanceResult(SyncStatus.Confirmed, observedExit.AddMinutes(4));
        var entry = new AttendanceEvent(Guid.NewGuid(), danna.Id, AttendanceAction.Entry, observedEntry);
        var exit = new AttendanceEvent(Guid.NewGuid(), sandy.Id, AttendanceAction.Exit, observedExit);

        FinishPresentation(show.PresentAttendanceAsync(entry, danna, new(SyncStatus.OutcomeUnknown)));
        FinishPresentation(show.PresentAttendanceAsync(entry, danna, new(SyncStatus.Unavailable)));
        FinishPresentation(show.PresentAttendanceAsync(entry, danna, new(SyncStatus.Confirmed)));
        Equal(false, confirmation.IsVisible);
        Equal(0, store.Rows().Length);

        var shortEntry = CaptureConfirmation(show, confirmation, entry, danna, local, destination, "entrada-corta.png", maximumLength: 50);
        Equal("Entrada de prueba · 09:00", receipt.Text);
        Equal(TimeSpan.FromSeconds(7), MainWindow.FeedbackDuration);
        Equal(true, show.IsFeedbackForEmployee(danna.Id));
        Equal(false, show.IsFeedbackForEmployee(sandy.Id));
        Equal(true, salutation.Text.Contains("Danna", StringComparison.Ordinal));
        Equal(false, salutation.Text.Contains("Ruiz", StringComparison.Ordinal));
        var count = store.Rows().Length;
        FinishPresentation(show.PresentAttendanceAsync(shortEntry, danna, local));
        Equal(count, store.Rows().Length);
        var selectedMessage = store.Rows()[^1].GetProperty("messageId").GetString();
        Equal(catalog.Messages.Single(message => message.Id == selectedMessage).Text,
            ((TextBlock)confirmation.FindName("Message")).Text);
        FinishPresentation(show.RecordIdentificationAsync(
            new(IdentificationStatus.Accepted, new(danna, Finger.RightIndex)), default));
        Equal(count, store.Rows().Length);
        Equal(false, confirmation.IsVisible);
        FinishPresentation(show.PresentAttendanceAsync(shortEntry, danna, local));
        show.ShowAttendanceReader(FlowState.Capturing);
        Equal(true, confirmation.IsVisible);
        FinishPresentation(show.RecordIdentificationAsync(new(IdentificationStatus.NoMatch), default));
        Equal(count, store.Rows().Length);
        Equal(false, show.IsFeedbackForEmployee(danna.Id));
        Equal(false, confirmation.IsVisible);
        FinishPresentation(show.PresentAttendanceAsync(shortEntry, danna, local));
        show.ShowAttendanceReader(FlowState.Matching);
        Equal(false, confirmation.IsVisible);
        FinishPresentation(show.PresentAttendanceAsync(shortEntry, danna, local));
        show.ShowAttendanceReader(FlowState.QualityRejected);
        Equal(false, confirmation.IsVisible);
        CaptureConfirmation(show, confirmation, entry, danna, local, destination, "entrada-larga.png", minimumLength: 72);

        CaptureConfirmation(show, confirmation, exit, sandy, officialExit, destination, "salida-corta.png", maximumLength: 50);
        Equal("Salida registrada · 18:04", receipt.Text);
        Equal(true, show.IsFeedbackForEmployee(sandy.Id));
        Equal(false, show.IsFeedbackForEmployee(danna.Id));
        Equal(true, salutation.Text.Contains("Sandy", StringComparison.Ordinal));
        Equal(false, salutation.Text.Contains("Danna", StringComparison.Ordinal));
        Equal(true, ((Grid)confirmation.FindName("ExitSymbol")).IsVisible);
        var longExit = CaptureConfirmation(show, confirmation, exit, sandy, officialExit, destination, "salida-larga.png", minimumLength: 75);

        show.WindowState = WindowState.Maximized;
        PumpUntil(() => show.WindowState == WindowState.Maximized && show.ActualWidth > 1000);
        FinishPresentation(show.PresentAttendanceAsync(longExit, sandy, officialExit));
        PumpUntil(() => confirmation.Opacity >= 0.999);
        Snapshot(show, Path.Combine(destination, "ventana-maximizada.png"), 1);
        show.WindowState = WindowState.Normal;
        show.Width = 790;
        show.Height = 515;
        CaptureConfirmation(show, confirmation, entry with { EmployeeId = longName.Id }, longName, local,
            destination, "ventana-compacta.png", minimumLength: 72);
        Equal(buildStamp, AssertBuildStamp(show));
        Equal(true, salutation.Text.Contains("Mariana-Alejandra-del-Carmen", StringComparison.Ordinal));
        Equal(false, salutation.Text.Contains("Martínez", StringComparison.Ordinal));
        show.ShowAttendanceReader(FlowState.Capturing, keepFeedback: false);
        Equal(false, confirmation.IsVisible);
        Equal("Coloca tu dedo en el lector", prompt.Text);
        show.Width = 1180;
        show.Height = 704;

        var navigation = (Button)show.FindName("NavButton");
        navigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((StackPanel)show.FindName("AdminHomePanel")).IsVisible);
        PumpUntil(() => ((TextBlock)show.FindName("AdminHomeStatus")).Text.StartsWith("Acceso pendiente", StringComparison.Ordinal));
        Equal(false, ((Button)show.FindName("SettingsButton")).IsEnabled);
        Equal(false, ((Button)show.FindName("EmployeesButton")).IsEnabled);
        Equal(false, ((Grid)show.FindName("ScanPanel")).IsVisible);
        Equal(false, ((Border)show.FindName("EnrollForm")).IsVisible);
        Equal(buildStamp, AssertBuildStamp(show));
        Snapshot(show, Path.Combine(destination, "administracion-sin-acceso.png"), 1);
        show.Close();
        PumpUntil(() => !show.IsVisible);

        CheckEditorialFallbackAndReplacement(store, catalog, entry, danna, local, exit, sandy, officialExit);
        var admin = new TestAdmin();
        show = new MainWindow(false, admin, admin);
        show.Show();
        Click(show, "NavButton");
        PumpUntil(() => ((StackPanel)show.FindName("AdminHomePanel")).IsVisible
            && ((Button)show.FindName("SettingsButton")).IsEnabled);
        Equal(0, admin.RosterReads);
        Equal(false, ((Grid)show.FindName("ScanPanel")).IsVisible);
        Snapshot(show, Path.Combine(destination, "administracion.png"), 1);

        Click(show, "SettingsButton");
        var toggle = (CheckBox)show.FindName("BreakToggle");
        Equal(true, toggle.IsVisible);
        Equal(false, ((ComboBox)show.FindName("Employees")).IsVisible);
        toggle.IsChecked = true;
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal("Incluye inicio y fin de descanso.", ((TextBlock)show.FindName("BreakDescription")).Text);
        Snapshot(show, Path.Combine(destination, "configuracion.png"), 1);

        Click(show, "NavButton");
        Click(show, "EmployeesButton");
        PumpUntil(() => ((Border)show.FindName("EnrollForm")).IsVisible);
        Equal(1, admin.RosterReads);
        Equal(false, toggle.IsVisible);
        Equal(false, ((Grid)show.FindName("ScanPanel")).IsVisible);
        var fingers = (ComboBox)show.FindName("Fingers");
        Equal(false, fingers.IsVisible);
        ((ComboBox)show.FindName("Employees")).SelectedIndex = 0;
        Equal(true, fingers.IsVisible);
        fingers.SelectedIndex = 1;
        Equal(false, ((Grid)show.FindName("ScanPanel")).IsVisible);
        Snapshot(show, Path.Combine(destination, "alta-empleado.png"), 1);
        show.Width = 820;
        show.Height = 525;
        Snapshot(show, Path.Combine(destination, "alta-empleado-alta-densidad.png"), 1.5);

        Click(show, "NavButton");
        Click(show, "SettingsButton");
        Equal<bool?>(true, toggle.IsChecked);
        Click(show, "NavButton");
        Click(show, "NavButton");
        PumpUntil(() => ((Grid)show.FindName("ScanPanel")).IsVisible);
        Equal(false, toggle.IsVisible);

        // Render the same vector control alone at a known progress, without simulating authorization.
        var art = new Border
        {
            Width = 170,
            Height = 170,
            Background = Brushes.White,
            Child = new FingerprintArt { Progress = 2d / 3 }
        };
        art.Measure(new Size(170, 170));
        art.Arrange(new Rect(0, 0, 170, 170));
        Snapshot(art, Path.Combine(destination, "progreso-vectorial-2-de-3.png"), 2);
        show.Close();
        PumpUntil(() => !show.IsVisible);
        Console.WriteLine("PASS WPF navigation, authorization, separate settings and enrollment, and snapshots");
    }

    private static string? AssertBuildStamp(MainWindow window)
    {
        window.UpdateLayout();
        var label = (TextBlock)window.FindName("DevelopmentBuildStamp");
        var stamp = BuildStamp.Read(TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City"));
        Equal(stamp is not null, label.IsVisible);
        if (stamp is null)
            return null;

        Equal(stamp, label.Text);
        var root = (Grid)window.Content;
        var corner = label.TranslatePoint(new Point(label.ActualWidth, label.ActualHeight), root);
        Equal(true, Math.Abs(root.ActualWidth - corner.X - label.Margin.Right) < 2);
        Equal(true, Math.Abs(root.ActualHeight - corner.Y - label.Margin.Bottom) < 2);
        return stamp;
    }

    private static AttendanceEvent CaptureConfirmation(MainWindow window, AttendanceConfirmation view, AttendanceEvent attendance,
        Employee employee, AttendanceResult result, string destination, string file, int minimumLength = 0,
        int maximumLength = int.MaxValue)
    {
        var message = (TextBlock)view.FindName("Message");
        for (var attempt = 0; attempt < 250; attempt++)
        {
            var mark = attendance with { Id = Guid.NewGuid() };
            FinishPresentation(window.PresentAttendanceAsync(mark, employee, result));
            if (message.Text.Length > 0 && message.Text.Length >= minimumLength && message.Text.Length <= maximumLength)
            {
                PumpUntil(() => view.Opacity >= 0.999);
                Equal(true, view.IsVisible);
                Snapshot(window, Path.Combine(destination, file), 1);
                return mark;
            }
        }
        throw new InvalidOperationException("No confirmation text matched the requested length.");
    }

    private static void FinishPresentation(Task pending)
    {
        PumpUntil(() => pending.IsCompleted);
        pending.GetAwaiter().GetResult();
    }

    private static void CheckEditorialFallbackAndReplacement(TemporaryEditorialStore store, MessageCatalog catalog,
        AttendanceEvent entry, Employee first, AttendanceResult local, AttendanceEvent exit, Employee second, AttendanceResult official)
    {
        using var release = new ManualResetEventSlim();
        var service = new EditorialMessages(() => catalog, environment =>
        {
            if (environment == EditorialEnvironment.Laboratory)
            {
                release.Wait(TimeSpan.FromSeconds(5));
                throw new IOException("Test-only editorial write failure.");
            }
            return new MessageHistory(store.DirectoryFor(environment));
        }, (_, _) => { }, _ => 0);
        var window = new MainWindow(false, null, null, service);
        window.Show();
        PumpUntil(() => ((TextBlock)window.FindName("ReaderText")).Text != "Comprobando…");
        var view = (AttendanceConfirmation)window.FindName("AttendanceReceipt");
        var message = (TextBlock)view.FindName("Message");
        var firstConfirmation = window.PresentAttendanceAsync(entry, first, local);
        try
        {
            Equal(true, window.IsFeedbackForEmployee(first.Id));
            Equal(false, message.IsVisible);
            Equal("Entrada de prueba · 09:00", ((TextBlock)view.FindName("Receipt")).Text);
            FinishPresentation(window.PresentAttendanceAsync(exit, second, official));
            Equal(false, firstConfirmation.IsCompleted);
            Equal(true, window.IsFeedbackForEmployee(second.Id));
            Equal(true, message.IsVisible);
            var latestText = message.Text;
            release.Set();
            FinishPresentation(firstConfirmation);
            Equal(latestText, message.Text);

            FinishPresentation(window.PresentAttendanceAsync(entry, first, local));
            Equal(true, window.IsFeedbackForEmployee(first.Id));
            Equal("Entrada de prueba · 09:00", ((TextBlock)view.FindName("Receipt")).Text);
            Equal(false, message.IsVisible);
        }
        finally
        {
            release.Set();
            FinishPresentation(firstConfirmation);
            window.Close();
            PumpUntil(() => !window.IsVisible);
        }
    }

    private static void Click(Window window, string button) =>
        ((Button)window.FindName(button)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private sealed class TestAdmin : IEnrollmentAuthorization, IEmployeeProvider
    {
        public int RosterReads { get; private set; }

        public Task<IEnrollmentSession?> AuthenticateAsync(CancellationToken cancellation) =>
            Task.FromResult<IEnrollmentSession?>(new TestSession());

        public Task<IReadOnlyList<Employee>> GetEmployeesAsync(IEnrollmentSession session, CancellationToken cancellation)
        {
            RosterReads++;
            return Task.FromResult<IReadOnlyList<Employee>>([new("ui-test", "Estilista de prueba")]);
        }
    }

    private static void Snapshot(FrameworkElement visual, string path, double scale)
    {
        visual.UpdateLayout();
        if (visual is Window window)
        {
            var logo = (Image)((DockPanel)((Border)((Grid)window.Content).Children[0]).Child).Children[0];
            if (logo.Source is not BitmapSource { PixelWidth: 700, PixelHeight: 200 })
                throw new InvalidOperationException("The official logo was not loaded.");
            visual = (FrameworkElement)window.Content;
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale),
            (int)Math.Ceiling(visual.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (limit.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException("WPF did not reach the expected state.");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(20);
        }
    }

    private static FingerprintTemplate Template(byte marker) => new([marker]);
    private static FingerEnrollment Enrollment(string id, Finger finger, params byte[] samples) =>
        new(new(new(id, "Prueba"), finger), samples.Select(Template).ToArray());
    private static EnrollmentStatus Accept(Biometrics.EnrollmentDraft draft, byte marker) =>
        draft.AcceptAsync(Template(marker), new Matcher(), default).GetAwaiter().GetResult();

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, found {actual}.");
    }

    private sealed class Matcher(bool allowModerate = false) : IFingerprintMatcher
    {
        public bool Matches(FingerprintTemplate left, FingerprintTemplate right, MatchLevel level) =>
            left.Bytes[0] == right.Bytes[0] || allowModerate && level == MatchLevel.Normal;
    }

    private sealed class TestSession : IEnrollmentSession
    {
        public bool IsActive { get; set; } = true;

        public Task<bool> CanEnrollAsync(string employeeId, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(IsActive);
        }

        public void Dispose() => IsActive = false;
    }
}
