namespace UProAttendance.Core;

public interface IAppointmentHoursProvider
{
    // Return null when the day's schedule is unavailable. The provider applies the salon's booking rules.
    Task<TimeSpan?> GetTotalAsync(DateOnly day, CancellationToken cancellation);
}

public sealed record DailyPunch(AttendanceEvent? Event, AttendanceResult? Result, AttendanceAction? Next);

public sealed class DailyAttendance(AttendanceSubmission submission)
{
    private readonly Dictionary<DateOnly, (bool Enabled, bool Manual)> breaks = [];
    private readonly Dictionary<(DateOnly Day, string EmployeeId), AttendanceAction> completed = [];
    private readonly Dictionary<(DateOnly Day, string EmployeeId), AttendanceEvent> uncertain = [];

    public bool HasBreak(DateOnly day) => breaks.TryGetValue(day, out var setting) && setting.Enabled;

    public void SetBreak(DateOnly day, bool enabled) => breaks[day] = (enabled, true);

    public void ApplyAppointmentHours(DateOnly day, TimeSpan? total)
    {
        if (total is { } hours && !(breaks.TryGetValue(day, out var setting) && setting.Manual))
            breaks[day] = (hours > TimeSpan.FromHours(6), false);
    }

    public async Task<DailyPunch> PunchAsync(DateOnly day, string employeeId, DateTimeOffset observedAt,
        CancellationToken cancellation)
    {
        var key = (day, employeeId);
        if (!uncertain.TryGetValue(key, out var punch))
        {
            if (Next(key) is not { } action)
                return new(null, null, null);

            punch = new(Guid.NewGuid(), employeeId, action, observedAt);
        }

        var result = await submission.SubmitAsync(punch, cancellation);
        switch (result.Status)
        {
            case SyncStatus.Confirmed:
            case SyncStatus.LocalPending:
                completed[key] = punch.Action;
                uncertain.Remove(key);
                break;
            case SyncStatus.OutcomeUnknown:
                uncertain[key] = punch;
                break;
        }

        return new(punch, result, Next(key));
    }

    private AttendanceAction? Next((DateOnly Day, string EmployeeId) key)
    {
        if (uncertain.TryGetValue(key, out var pending))
            return pending.Action;
        if (!completed.TryGetValue(key, out var previous))
            return AttendanceAction.Entry;

        return previous switch
        {
            AttendanceAction.Entry => HasBreak(key.Day) ? AttendanceAction.BreakStart : AttendanceAction.Exit,
            AttendanceAction.BreakStart => AttendanceAction.BreakEnd,
            AttendanceAction.BreakEnd => AttendanceAction.Exit,
            _ => null
        };
    }
}
