using System.Diagnostics;
using UProAttendance.Core;
using UProAttendance.Hardware;

namespace UProAttendance.Presentation;

internal sealed class ReaderWorkflow(Biometrics biometrics)
{
    private int lifts;
    private int? attendanceLift;

    public void FingerLifted() => Interlocked.Increment(ref lifts);

    public Task WatchAttendanceAsync(IntPtr eventTarget, Action<FlowState> update,
        Func<IdentificationResult, Task> identified, CancellationToken cancellation) =>
        Task.Run(async () =>
        {
            using var reader = SecuGenReader.Open();
            reader.EnableContactEvents(eventTarget);
            if (attendanceLift is { } previous && Volatile.Read(ref lifts) == previous)
                update(FlowState.WaitingForLift);

            while (true)
            {
                if (attendanceLift is { } afterCapture)
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
                    var ticks = 0;
                    while (Volatile.Read(ref lifts) == afterCapture)
                    {
                        await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false);
                        if (++ticks % 10 == 0 && WindowsReader.IsStarted() == false)
                            throw new ReaderFailure(new(ReaderStatus.Absent));
                    }
                    attendanceLift = null;
                }

                update(FlowState.Capturing);
                Capture capture;
                do
                {
                    capture = reader.Capture(cancellation);
                    if (capture.Status == CaptureStatus.QualityRejected)
                        update(FlowState.QualityRejected);
                    if (capture.Status == CaptureStatus.TimedOut && WindowsReader.IsStarted() == false)
                        throw new ReaderFailure(new(ReaderStatus.Absent));
                }
                while (capture.Template is null);

                attendanceLift = Volatile.Read(ref lifts);
                IdentificationResult result;
                using (capture.Template)
                {
                    update(FlowState.Matching);
                    result = biometrics.Identify(capture.Template, reader, cancellation);
                }
                await identified(result).ConfigureAwait(false);
            }
        }, cancellation);

    public Task<EnrollmentStatus> EnrollAsync(Employee employee, Finger finger, IEnrollmentSession authorization,
        IntPtr eventTarget, Action<int> accepted, Action<bool> waitingForLift,
        Action<FlowState> update, CancellationToken cancellation) =>
        Task.Run(async () =>
        {
            if (!authorization.IsActive
                || !await authorization.CanEnrollAsync(employee.Id, cancellation).ConfigureAwait(false))
                return EnrollmentStatus.Denied;

            using var reader = SecuGenReader.Open();
            reader.EnableContactEvents(eventTarget);
            using var draft = biometrics.Begin(employee, finger, authorization);
            while (true)
            {
                var sample = ReadValid(reader, cancellation, update);
                var afterCapture = Volatile.Read(ref lifts);
                update(FlowState.Matching);
                var status = await draft.AcceptAsync(sample, reader, cancellation).ConfigureAwait(false);
                if (status is EnrollmentStatus.Accepted or EnrollmentStatus.Completed)
                    accepted(draft.AcceptedCount);
                if (status is not (EnrollmentStatus.Accepted or EnrollmentStatus.Inconsistent))
                    return status;

                update(status == EnrollmentStatus.Inconsistent ? FlowState.QualityRejected : FlowState.EnrollmentInProgress);
                waitingForLift(status == EnrollmentStatus.Inconsistent);
                await WaitForLiftAsync(afterCapture, cancellation).ConfigureAwait(false);
                update(FlowState.Capturing);
            }
        }, cancellation);

    private static FingerprintTemplate ReadValid(IFingerprintCapture reader, CancellationToken cancellation,
        Action<FlowState>? update = null)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(18))
        {
            var result = reader.Capture(cancellation);
            if (result.Template is { } sample)
                return sample;
            if (result.Status == CaptureStatus.QualityRejected)
                update?.Invoke(FlowState.QualityRejected);
        }
        throw new TimeoutException("Tiempo de captura agotado. Vuelve a colocar el dedo.");
    }

    private async Task WaitForLiftAsync(int previous, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref lifts) == previous)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
            {
                if (WindowsReader.IsStarted() == false)
                    throw new ReaderFailure(new(ReaderStatus.Absent));
                throw new TimeoutException("El lector no notificó que se retiró el dedo. Inicia de nuevo.");
            }
            await Task.Delay(80, cancellation).ConfigureAwait(false);
        }
    }
}
