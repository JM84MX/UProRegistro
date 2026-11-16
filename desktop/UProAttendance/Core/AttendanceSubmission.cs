namespace UProAttendance.Core;

public sealed class AttendanceSubmission(IAttendanceGateway gateway)
{
    private readonly Dictionary<Guid, AttendanceEvent> pending = [];

    public IReadOnlyCollection<AttendanceEvent> Pending => pending.Values;

    // The application serializes submissions. Reconciliation can reuse a pending event verbatim.
    public async Task<AttendanceResult> SubmitAsync(AttendanceEvent request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var wasPending = pending.ContainsKey(request.Id);
        pending.TryAdd(request.Id, request);
        AttendanceResult result;
        try
        {
            result = await gateway.SubmitAsync(request, cancellation);
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException
            || error is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            result = new(SyncStatus.OutcomeUnknown);
        }

        if (result.Status == SyncStatus.Confirmed && result.OfficialTime is null)
            result = new(SyncStatus.OutcomeUnknown);

        if (result.Status == SyncStatus.Confirmed || result.Status == SyncStatus.Unavailable && !wasPending)
            pending.Remove(request.Id);
        else if (result.Status == SyncStatus.Unavailable)
            result = new(SyncStatus.OutcomeUnknown);

        return result;
    }
}
