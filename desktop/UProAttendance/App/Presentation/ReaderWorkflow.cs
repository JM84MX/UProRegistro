using System.Diagnostics;
using UProAttendance.Core;
using UProAttendance.Hardware;

namespace UProAttendance.Presentation;

internal sealed class ReaderWorkflow(Biometrics biometrics)
{
    private int lifts;

    public void FingerLifted() => Interlocked.Increment(ref lifts);

    public Task<IdentificationResult> IdentifyAsync(Action matching, Action qualityRejected, CancellationToken cancellation) =>
        Task.Run(() =>
        {
            using var reader = SecuGenReader.Open();
            using var sample = ReadValid(reader, cancellation, _ => qualityRejected());
            matching();
            return biometrics.Identify(sample, reader, cancellation);
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
