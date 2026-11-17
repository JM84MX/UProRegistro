using System.Security.Principal;
using UProAttendance.Core;

namespace UProAttendance;

internal sealed class UnconfiguredUproApp : IEnrollmentAuthorization, IEmployeeProvider, IAttendanceGateway,
    IAppointmentHoursProvider
{
    public Task<TimeSpan?> GetTotalAsync(DateOnly day, CancellationToken cancellation) =>
        Task.FromResult<TimeSpan?>(null);

    public Task<IEnrollmentSession?> AuthenticateAsync(CancellationToken cancellation) =>
        Task.FromResult<IEnrollmentSession?>(null);

    public Task<IReadOnlyList<Employee>> GetEmployeesAsync(IEnrollmentSession session, CancellationToken cancellation) =>
        Task.FromException<IReadOnlyList<Employee>>(new InvalidOperationException("Proveedor de empleados pendiente."));

    public Task<AttendanceResult> SubmitAsync(AttendanceEvent attendance, CancellationToken cancellation) =>
        Task.FromResult(new AttendanceResult(SyncStatus.Unavailable));
}

#if DEBUG
internal sealed class Laboratory : IEnrollmentAuthorization, IEmployeeProvider, IAttendanceGateway
{
    private readonly Employee[] employees =
    [
        new("lab-01", "Estilista de prueba A") { Position = EmployeePosition.Stylist },
        new("lab-02", "Estilista de prueba B") { Position = EmployeePosition.Stylist },
        new("lab-03", "Recepción de prueba") { Position = EmployeePosition.Receptionist },
        new("lab-04", "Asistente de prueba") { Position = EmployeePosition.Assistant }
    ];

    public Task<IEnrollmentSession?> AuthenticateAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var identity = WindowsIdentity.GetCurrent();
        var isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        return Task.FromResult<IEnrollmentSession?>(isAdmin ? new Session(employees) : null);
    }

    public Task<IReadOnlyList<Employee>> GetEmployeesAsync(IEnrollmentSession session, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (session is not Session { IsActive: true })
            throw new UnauthorizedAccessException();
        return Task.FromResult<IReadOnlyList<Employee>>(employees);
    }

    public Task<AttendanceResult> SubmitAsync(AttendanceEvent attendance, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(new AttendanceResult(SyncStatus.LocalPending));
    }

    private sealed class Session(Employee[] employees) : IEnrollmentSession
    {
        public bool IsActive { get; private set; } = true;

        public Task<bool> CanEnrollAsync(string employeeId, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(IsActive && employees.Any(employee => employee.Id == employeeId));
        }

        public void Dispose() => IsActive = false;
    }
}
#endif
