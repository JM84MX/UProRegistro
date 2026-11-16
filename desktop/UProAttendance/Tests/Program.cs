using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UProAttendance;
using UProAttendance.Core;
using UProAttendance.Hardware;
using UProAttendance.Presentation;

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
                ("Unavailable and local-only submissions cannot acquire an official time", AttendanceLocal)
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
        var show = new MainWindow(false);
        show.Show();
        PumpUntil(() => ((TextBlock)show.FindName("ReaderText")).Text != "Comprobando…");
        Equal("Registro de asistencia", ((TextBlock)show.FindName("TitleText")).Text);
        Equal(false, ((Button)show.FindName("EntryButton")).IsEnabled);
        Snapshot(show, Path.Combine(destination, "registro-de-asistencia.png"), 1);

        var navigation = (Button)show.FindName("NavButton");
        navigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((TextBlock)show.FindName("TitleText")).Text == "Registrar huellas");
        ((Button)show.FindName("AuthorizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ((TextBlock)show.FindName("AdminDetail")).Text.StartsWith("Acceso pendiente", StringComparison.Ordinal));
        Equal(Visibility.Collapsed, ((StackPanel)show.FindName("EnrollForm")).Visibility);
        Snapshot(show, Path.Combine(destination, "registrar-huellas.png"), 1);
        show.Width = 820;
        show.Height = 525;
        Snapshot(show, Path.Combine(destination, "registrar-huellas-alta-densidad.png"), 1.5);

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
        Console.WriteLine("PASS WPF navigation, fail-closed authorization, logo, and offscreen snapshots");
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
