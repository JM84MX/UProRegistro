namespace UProAttendance.Core.Editorial;

public enum EditorialEnvironment { Laboratory, Operational }
public enum EditorialIssue { CatalogUnavailable, HistoryUnavailable }

public sealed class EditorialMessages(
    Func<MessageCatalog> loadCatalog,
    Func<EditorialEnvironment, MessageHistory> historyFor,
    Action<EditorialEnvironment, EditorialIssue> reportIssue,
    Func<int, int>? choose = null)
{
    private readonly Lazy<MessageCatalog> catalog = new(loadCatalog);
    private readonly Func<int, int> choose = choose ?? Random.Shared.Next;

    public string? Select(AttendanceEvent attendance, Employee employee, AttendanceResult result)
    {
        var eventName = attendance.Action switch
        {
            AttendanceAction.Entry => "entry",
            AttendanceAction.Exit => "exit",
            _ => null
        };
        if (eventName is null || attendance.Id == Guid.Empty || string.IsNullOrWhiteSpace(employee.Id)
            || employee.Id != attendance.EmployeeId
            || result is not ({ Status: SyncStatus.LocalPending } or { Status: SyncStatus.Confirmed, OfficialTime: not null }))
            return null;

        var environment = result.Status == SyncStatus.LocalPending && !result.Operational
            ? EditorialEnvironment.Laboratory : EditorialEnvironment.Operational;
        MessageCatalog available;
        try
        {
            available = catalog.Value;
        }
        catch (Exception)
        {
            Report(environment, EditorialIssue.CatalogUnavailable);
            return null;
        }

        try
        {
            return historyFor(environment).Assign(attendance, employee, eventName, available, choose)?.Text;
        }
        catch (Exception)
        {
            Report(environment, EditorialIssue.HistoryUnavailable);
            return null;
        }
    }

    private void Report(EditorialEnvironment environment, EditorialIssue issue)
    {
        try
        {
            reportIssue(environment, issue);
        }
        catch (Exception)
        {
            // Editorial diagnostics are best-effort after a successful attendance operation.
        }
    }
}
