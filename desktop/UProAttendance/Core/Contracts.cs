using System.Security.Cryptography;

namespace UProAttendance.Core;

public enum FlowState
{
    Idle, Capturing, QualityRejected, Matching, Identified, EnrollmentInProgress,
    Success, Failed, DeviceUnavailable
}

public enum ReaderStatus { Ready, Absent, WindowsError, SdkMissing, SdkError, Busy }
public sealed record ReaderState(ReaderStatus Status, uint? Code = null, string? Operation = null);
public enum CaptureStatus { Accepted, TimedOut, QualityRejected }
public enum MatchLevel { Normal, High }
public enum Finger
{
    RightThumb = 1, RightIndex, RightMiddle, RightRing, RightLittle,
    LeftThumb, LeftIndex, LeftMiddle, LeftRing, LeftLittle
}

public sealed class FingerprintTemplate(byte[] ownedBytes) : IDisposable
{
    private byte[]? bytes = ownedBytes;
    public byte[] Bytes => bytes ?? throw new ObjectDisposedException(nameof(FingerprintTemplate));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref bytes, null) is { } buffer)
            CryptographicOperations.ZeroMemory(buffer);
    }
}

public sealed record Capture(CaptureStatus Status, FingerprintTemplate? Template = null);
public sealed record Employee(string Id, string Name);
public sealed record Identification(Employee Employee, Finger Finger);
public sealed record FingerEnrollment(Identification Identity, IReadOnlyList<FingerprintTemplate> Samples);

public interface IFingerprintCapture
{
    Capture Capture(CancellationToken cancellation);
}

public interface IFingerprintMatcher
{
    bool Matches(FingerprintTemplate left, FingerprintTemplate right, MatchLevel level);
}

// The gallery owns saved samples. Callers serialize access and dispose the gallery after draining operations.
public interface ITemplateGallery
{
    IReadOnlyList<FingerEnrollment> Enrollments { get; }
    void Add(FingerEnrollment enrollment);
}

public interface IEnrollmentAuthorization
{
    Task<IEnrollmentSession?> AuthenticateAsync(CancellationToken cancellation);
}

public interface IEnrollmentSession : IDisposable
{
    bool IsActive { get; }
    Task<bool> CanEnrollAsync(string employeeId, CancellationToken cancellation);
}

public interface IEmployeeProvider
{
    Task<IReadOnlyList<Employee>> GetEmployeesAsync(IEnrollmentSession session, CancellationToken cancellation);
}

public enum AttendanceAction { Entry, Exit }
public enum SyncStatus { Unavailable, LocalPending, OutcomeUnknown, Confirmed }
public sealed record AttendanceEvent(Guid Id, string EmployeeId, AttendanceAction Action, DateTimeOffset ObservedOnDevice);
public sealed record AttendanceResult(SyncStatus Status, DateTimeOffset? OfficialTime = null);

public interface IAttendanceGateway
{
    // Retries and reconciliation use the same event ID. A transport timeout may be OutcomeUnknown.
    Task<AttendanceResult> SubmitAsync(AttendanceEvent attendance, CancellationToken cancellation);
}
