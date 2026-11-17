using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UProAttendance.Core.Editorial;

public sealed class MessageHistory(string directory)
{
    private string Primary => Path.Combine(directory, "history.json");
    private string Backup => Path.Combine(directory, "history.backup.json");

    internal CatalogMessage? Assign(AttendanceEvent attendance, Employee employee, string eventName,
        MessageCatalog catalog, Func<int, int> choose)
    {
        using var lockFile = AcquireLock();
        var history = Read();
        var existing = history.Assignments.Find(row => row.AttendanceId == attendance.Id);
        if (existing is not null)
        {
            if (existing.EmployeeId != employee.Id || existing.Event != eventName)
                throw new InvalidDataException("An editorial assignment has conflicting attendance details.");
            var message = catalog.Find(existing.MessageId);
            return message?.Event == eventName ? message : null;
        }

        var eligible = catalog.Eligible(employee.Position, eventName);
        if (eligible.Count == 0)
            return null;

        // Keep previous cycles and retired IDs. Only the current cycle is used to exclude candidates.
        var lane = history.Assignments.Where(row => row.EmployeeId == employee.Id && row.Event == eventName).ToArray();
        var last = lane.LastOrDefault();
        var cycle = last?.Cycle ?? 1;
        var consumed = lane.Where(row => row.Cycle == cycle).Select(row => row.MessageId).ToHashSet(StringComparer.Ordinal);
        var candidates = eligible.Where(message => !consumed.Contains(message.Id)).ToArray();
        if (candidates.Length == 0)
        {
            cycle = checked(cycle + 1);
            candidates = eligible.Where(message => eligible.Count == 1 || message.Id != last?.MessageId).ToArray();
        }

        var selected = candidates[choose(candidates.Length)];
        history.Assignments.Add(new(employee.Id, eventName, selected.Id, cycle, history.Assignments.Count + 1L, attendance.Id));
        Save(history);
        return selected;
    }

    private FileStream AcquireLock()
    {
        var elapsed = Stopwatch.StartNew();
        var path = Path.Combine(directory, "history.lock");
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33 && elapsed.ElapsedMilliseconds < 600)
            {
                Thread.Sleep(25);
            }
        }
    }

    private HistoryDocument Read()
    {
        FileStream source;
        try
        {
            source = File.OpenRead(Primary);
        }
        catch (FileNotFoundException)
        {
            // A backup without a primary needs an explicit recovery decision.
            try
            {
                using var backup = File.OpenRead(Backup);
                throw new InvalidDataException("The editorial history exists only as a backup.");
            }
            catch (FileNotFoundException)
            {
                return new(1, []);
            }
        }

        using (source)
        {
            var history = JsonSerializer.Deserialize<HistoryDocument>(source, MessageCatalog.JsonOptions);
            if (history is not { SchemaVersion: 1, Assignments: not null })
                throw new InvalidDataException("Unsupported or incomplete editorial history.");

            var attendanceIds = new HashSet<Guid>();
            var cycles = new Dictionary<(string Employee, string Event), (int Number, HashSet<string> Messages)>();
            for (var i = 0; i < history.Assignments.Count; i++)
            {
                var row = history.Assignments[i];
                if (row is null || string.IsNullOrWhiteSpace(row.EmployeeId) || string.IsNullOrWhiteSpace(row.MessageId)
                    || row.Event is not ("entry" or "exit") || row.Order != i + 1L
                    || row.AttendanceId == Guid.Empty || !attendanceIds.Add(row.AttendanceId))
                    throw new InvalidDataException("Invalid editorial history assignment.");

                var lane = (row.EmployeeId, row.Event);
                if (!cycles.TryGetValue(lane, out var cycle))
                    cycle = (1, new(StringComparer.Ordinal));
                if (row.Cycle > cycle.Number && row.Cycle - cycle.Number == 1 && cycle.Messages.Count > 0)
                    cycle = (row.Cycle, new(StringComparer.Ordinal));
                if (row.Cycle != cycle.Number || !cycle.Messages.Add(row.MessageId))
                    throw new InvalidDataException("Invalid editorial history cycle.");
                cycles[lane] = cycle;
            }

            return history;
        }
    }

    private void Save(HistoryDocument history)
    {
        var temporary = Path.Combine(directory, $"history.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(target, history, MessageCatalog.JsonOptions);
                target.Flush(flushToDisk: true);
            }

            if (File.Exists(Primary))
                File.Replace(temporary, Primary, Backup);
            else
                File.Move(temporary, Primary);
        }
        finally
        {
            // A failed write must leave the previous history intact. A stale temp file is never read.
            File.Delete(temporary);
        }
    }

    private sealed record HistoryDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] List<Assignment> Assignments);

    private sealed record Assignment(
        [property: JsonRequired] string EmployeeId,
        [property: JsonRequired] string Event,
        [property: JsonRequired] string MessageId,
        [property: JsonRequired] int Cycle,
        [property: JsonRequired] long Order,
        [property: JsonRequired] Guid AttendanceId);
}
