using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using UProAttendance;
using UProAttendance.Core;
using UProAttendance.Core.Editorial;

internal static class EditorialTests
{
    public static readonly (string Name, Action Body)[] All =
    [
        ("Bundled file is byte-identical and matches the six profile/event totals", BundledCatalog),
        ("Malformed catalogs are rejected and multi-profile IDs are included once", CatalogValidation),
        ("Stylist and receptionist get 250 messages per event; other positions get 150", EligiblePools),
        ("The first 250 messages are unique and the next cycle retains history without an immediate repeat", FullCycle),
        ("Employee IDs, event, and restarts keep independent assignment histories", IndependentHistories),
        ("Reordering, additions, edits, retirement and position changes retain prior IDs", CatalogChanges),
        ("Only accepted entry or exit consumes a message and the same punch reuses its selection", AcceptedMarksOnly),
        ("History failures preserve the successful attendance result and the prior file", HistoryFailures),
        ("A malformed or missing primary is preserved until explicit recovery", ControlledRecovery),
        ("Concurrent writers serialize and deduplicate the same accepted mark", ConcurrentWriters),
        ("Laboratory and operational histories never share an assignment", EnvironmentsIsolated),
        ("An offline operational mark reuses its editorial assignment after confirmation", OfflineAssignment),
        ("Windows history directories and files use only the current SID and SYSTEM", WindowsPermissions)
    ];

    private static readonly AttendanceResult Local = new(SyncStatus.LocalPending);
    private static readonly AttendanceResult Official = new(SyncStatus.Confirmed, DateTimeOffset.UnixEpoch);
    private static Employee Stylist => new("a", "María Ruiz") { Position = EmployeePosition.Stylist };

    private static void BundledCatalog()
    {
        using var bundled = MessageCatalog.OpenBundledResource();
        Check("9875261F258072140C37F53A644AFC746B94279F0A6AC808FADB591321B61368",
            Convert.ToHexString(SHA256.HashData(bundled)));
        var catalog = MessageCatalog.LoadBundled();
        Check(700, catalog.Messages.Count);
        Check(700, catalog.Messages.Select(message => message.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var name in new[] { "entry", "exit" })
        {
            Check(150, catalog.Messages.Count(message => message.Event == name && message.Profiles.SequenceEqual(["all"])));
            Check(100, catalog.Messages.Count(message => message.Event == name && message.Profiles.SequenceEqual(["stylist"])));
            Check(100, catalog.Messages.Count(message => message.Event == name && message.Profiles.SequenceEqual(["receptionist"])));
        }
    }

    private static void CatalogValidation()
    {
        string[] malformed =
        [
            """{"schemaVersion":2,"locale":"es-MX","messages":[]}""",
            """{"schemaVersion":1,"messages":[]}""",
            """{"schemaVersion":1,"locale":"es-MX","messages":null}""",
            """{"schemaVersion":1,"locale":"es-MX","messages":[{"id":" ","profiles":["all"],"event":"entry","text":"x"}]}""",
            """{"schemaVersion":1,"locale":"es-MX","messages":[{"id":"x","profiles":["invalid"],"event":"entry","text":"x"}]}""",
            """{"schemaVersion":1,"locale":"es-MX","messages":[{"id":"x","profiles":["all"],"event":"break","text":"x"}]}""",
            """{"schemaVersion":1,"locale":"es-MX","messages":[{"id":"x","profiles":["all"],"event":"entry","text":" "}]}""",
            """{"schemaVersion":1,"locale":"es-MX","messages":[{"id":"x","profiles":["all"],"event":"entry"}]}""",
            """{"schemaVersion":1,"locale":"es-MX","messages":[{"id":"x","id":"y","profiles":["all"],"event":"entry","text":"x"}]}""",
            """{"schemaVersion":1,"locale":"es-MX","messages":["""
        ];
        foreach (var json in malformed)
            Reject(() => Parse(json));
        var shared = new CatalogMessage("EXIT-looks-like-exit", ["all", "stylist"], "entry", "Con acentos, ¿verdad?");
        Reject(() => Catalog(shared, shared with { Event = "exit" }));
        var fromProperties = Catalog(shared);
        Check(1, fromProperties.Eligible(EmployeePosition.Stylist, "entry").Count);
        Check(0, fromProperties.Eligible(EmployeePosition.Stylist, "exit").Count);
    }

    private static void EligiblePools()
    {
        var catalog = MessageCatalog.LoadBundled();
        foreach (var name in new[] { "entry", "exit" })
        {
            foreach (var position in Enum.GetValues<EmployeePosition>().Append((EmployeePosition)123))
            {
                var eligible = catalog.Eligible(position, name);
                var expected = position is EmployeePosition.Stylist or EmployeePosition.Receptionist ? 250 : 150;
                Check(expected, eligible.Select(message => message.Id).Distinct(StringComparer.Ordinal).Count());
                Check(true, eligible.All(message => message.Event == name));
                if (expected == 150)
                    Check(true, eligible.All(message => message.Profiles.Contains("all")));
            }
        }
    }

    private static void FullCycle()
    {
        using var store = new TemporaryEditorialStore();
        var catalog = MessageCatalog.LoadBundled();
        var eligible = catalog.Eligible(Stylist.Position, "entry");
        var lastIndex = false;
        var messages = store.Service(catalog, choose: length => lastIndex ? length - 1 : 0);
        for (var i = 0; i < eligible.Count; i++)
            Check(eligible[i].Text, messages.Select(Mark(Stylist), Stylist, Local));

        var rows = store.Rows();
        Check(250, rows.Select(row => row.GetProperty("messageId").GetString()).Distinct().Count());
        Check(true, rows.All(row => row.GetProperty("cycle").GetInt32() == 1));

        lastIndex = true;
        Check(eligible[^2].Text, messages.Select(Mark(Stylist), Stylist, Local));
        rows = store.Rows();
        Check(251, rows.Length);
        Check(2, rows[^1].GetProperty("cycle").GetInt32());
        Check(eligible[^2].Id, rows[^1].GetProperty("messageId").GetString());
    }

    private static void IndependentHistories()
    {
        using var store = new TemporaryEditorialStore();
        var catalog = Catalog(Entry("a", "Primera"), Entry("b", "Segunda"), Exit("z", "Despedida"));
        var employee = Stylist;
        var other = employee with { Id = "other" };
        var messages = store.Service(catalog);
        Check("Primera", messages.Select(Mark(employee), employee, Local));
        Check("Primera", messages.Select(Mark(other), other, Local));
        Check("Despedida", messages.Select(Mark(employee, AttendanceAction.Exit), employee, Local));
        var reopened = store.Service(catalog);
        var renamed = employee with { Name = "Nombre cambiado" };
        Check("Segunda", reopened.Select(Mark(renamed), renamed, Local));
        Check(4, store.Rows().Length);
        Check(false, File.ReadAllText(store.HistoryPath()).Contains("María", StringComparison.Ordinal));
        Check(false, File.ReadAllText(store.HistoryPath()).Contains("Primera", StringComparison.Ordinal));
    }

    private static void CatalogChanges()
    {
        using var store = new TemporaryEditorialStore();
        var employee = Stylist;
        var mark = Mark(employee);
        var original = store.Service(Catalog(Entry("shared", "Texto anterior"), Entry("s", "Salón", "stylist")));
        Check("Texto anterior", original.Select(mark, employee, Local));
        Check("Salón", original.Select(Mark(employee), employee, Local));
        var changed = store.Service(Catalog(Entry("s", "Salón", "stylist"), Entry("shared", "  Texto editado {literal}…  "),
            Entry("new", "Recepción", "receptionist")));
        var newPosition = employee with { Position = EmployeePosition.Receptionist };
        Check("  Texto editado {literal}…  ", changed.Select(mark, newPosition, Local));
        Check("Recepción", changed.Select(Mark(newPosition), newPosition, Local));
        Check(3, store.Rows().Length);
        Check(true, store.Rows().All(row => row.GetProperty("cycle").GetInt32() == 1));
        var retired = store.Service(Catalog(Entry("new", "Recepción", "receptionist")));
        Check<string?>(null, retired.Select(mark, newPosition, Local));
        Check(3, store.Rows().Length);
    }

    private static void AcceptedMarksOnly()
    {
        using var store = new TemporaryEditorialStore();
        var messages = store.Service(Catalog(Entry("a", "¿Qué tal?  {Literal}"), Entry("b", "Otro mensaje")));
        var employee = Stylist;
        var mark = Mark(employee);
        foreach (var status in new[] { SyncStatus.Unavailable, SyncStatus.OutcomeUnknown, SyncStatus.Confirmed })
            Check<string?>(null, messages.Select(mark, employee, new(status)));
        Check<string?>(null, messages.Select(mark with { Id = Guid.Empty }, employee, Local));
        Check<string?>(null, messages.Select(mark, employee with { Id = "wrong" }, Local));
        Check<string?>(null, messages.Select(mark with { Action = AttendanceAction.BreakStart }, employee, Local));
        Check<string?>(null, messages.Select(mark with { Action = AttendanceAction.BreakEnd }, employee, Local));
        Check(0, store.Rows().Length);
        Check("¿Qué tal?  {Literal}", messages.Select(mark, employee, Local));
        Check("¿Qué tal?  {Literal}", store.Service(Catalog(Entry("a", "¿Qué tal?  {Literal}")))
            .Select(mark, employee, Local));
        Check(1, store.Rows().Length);
    }

    private static void HistoryFailures()
    {
        using var store = new TemporaryEditorialStore();
        var issues = new List<EditorialIssue>();
        var catalog = Catalog(Entry("a", "Original"), Entry("b", "Siguiente"));
        var messages = store.Service(catalog, report: (_, issue) => issues.Add(issue));
        var employee = Stylist;
        Check("Original", messages.Select(Mark(employee), employee, Local));
        var previous = File.ReadAllText(store.HistoryPath());

        // A second writer owns the history; attendance itself can still accept another event.
        using (var hold = File.Open(Path.Combine(store.DirectoryFor(EditorialEnvironment.Laboratory), "history.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var submitted = new DailyAttendance(new(new LocalGateway()))
                .PunchAsync(new DateOnly(2026, 11, 16), employee.Id, DateTimeOffset.UnixEpoch, default).GetAwaiter().GetResult();
            Check(SyncStatus.LocalPending, submitted.Result!.Status);
            Check<string?>(null, messages.Select(submitted.Event!, employee, submitted.Result));
            Check(EditorialIssue.HistoryUnavailable, issues.Single());
            Check(previous, File.ReadAllText(store.HistoryPath()));
        }

        Check("Siguiente", messages.Select(Mark(employee), employee, Local));
        var brokenCatalog = new EditorialMessages(() => throw new JsonException(), _ => throw new IOException(),
            (_, issue) => issues.Add(issue), _ => 0);
        Check<string?>(null, brokenCatalog.Select(Mark(employee), employee, Local));
        Check(EditorialIssue.CatalogUnavailable, issues.Last());
    }

    private static void ControlledRecovery()
    {
        using var store = new TemporaryEditorialStore();
        var messages = store.Service(Catalog(Entry("a", "Original"), Entry("b", "Segundo"), Entry("c", "Tercero")));
        var employee = Stylist;
        Check("Original", messages.Select(Mark(employee), employee, Local));
        Check("Segundo", messages.Select(Mark(employee), employee, Local));
        var primary = store.HistoryPath();
        var valid = File.ReadAllText(primary);
        var backup = Path.Combine(store.DirectoryFor(EditorialEnvironment.Laboratory), "history.backup.json");
        var backupText = File.ReadAllText(backup);
        File.WriteAllText(primary, "{corrupt");
        Check<string?>(null, messages.Select(Mark(employee), employee, Local));
        Check("{corrupt", File.ReadAllText(primary));
        Check(backupText, File.ReadAllText(backup));
        File.Move(primary, primary + ".damaged");
        Check<string?>(null, messages.Select(Mark(employee), employee, Local));
        Check(false, File.Exists(primary));
        File.WriteAllText(primary, valid);
        Check("Tercero", messages.Select(Mark(employee), employee, Local));
        Check(3, store.Rows().Length);
    }

    private static void ConcurrentWriters()
    {
        using var store = new TemporaryEditorialStore();
        var catalog = Catalog(Entry("a", "Primero"), Entry("b", "Segundo"), Entry("c", "Tercero"));
        var employee = Stylist;
        var repeated = Mark(employee);
        var marks = new[] { repeated, Mark(employee), repeated, Mark(employee) };
        var texts = new string?[marks.Length];
        Parallel.For(0, marks.Length, i => texts[i] = store.Service(catalog).Select(marks[i], employee, Local));
        Check(true, texts.All(text => text is not null));
        Check(texts[0], texts[2]);
        Check(3, store.Rows().Length);
    }

    private static void EnvironmentsIsolated()
    {
        using var store = new TemporaryEditorialStore();
        var messages = store.Service(Catalog(Entry("a", "Primera"), Entry("b", "Segunda")));
        var employee = Stylist;
        var sameMark = Mark(employee);
        Check("Primera", messages.Select(sameMark, employee, Local));
        Check("Segunda", messages.Select(Mark(employee), employee, Local));
        Check("Primera", messages.Select(sameMark, employee, Official));
        Check(2, store.Rows().Length);
        Check(1, store.Rows(EditorialEnvironment.Operational).Length);
    }

    private static void WindowsPermissions()
    {
        using var store = new TemporaryEditorialStore();
        var directory = new DirectoryInfo(LocalEditorialHistory.ProtectDirectory(
            store.DirectoryFor(EditorialEnvironment.Laboratory)));
        using var identity = WindowsIdentity.GetCurrent();
        var expected = new[] { identity.User!.Value, "S-1-5-18" }.Order().ToArray();
        var security = directory.GetAccessControl();
        Check(true, security.AreAccessRulesProtected);
        Check(true, ReadSids(security).SequenceEqual(expected));

        var messages = store.Service(Catalog(Entry("a", "Primera"), Entry("b", "Segunda")));
        var employee = Stylist;
        Check("Primera", messages.Select(Mark(employee), employee, Local));
        Check("Segunda", messages.Select(Mark(employee), employee, Local));
        foreach (var file in directory.EnumerateFiles())
            Check(true, ReadSids(file.GetAccessControl()).SequenceEqual(expected));
    }

    private static void OfflineAssignment()
    {
        using var store = new TemporaryEditorialStore();
        var catalog = Catalog(Entry("a", "Primera"), Entry("b", "Segunda"));
        var mark = Mark(Stylist);
        Check("Primera", store.Service(catalog).Select(mark, Stylist, Local with { Operational = true }));
        Check("Primera", store.Service(catalog).Select(mark, Stylist, Official));
        Check(1, store.Rows(EditorialEnvironment.Operational).Length);
        Check(0, store.Rows(EditorialEnvironment.Laboratory).Length);
    }

    private static IEnumerable<string> ReadSids(FileSystemSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>().Select(rule => rule.IdentityReference.Value).Distinct().Order();

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is JsonException or InvalidDataException) { return; }
        throw new InvalidOperationException("The malformed catalog was accepted.");
    }

    private static MessageCatalog Catalog(params CatalogMessage[] messages) => Parse(JsonSerializer.Serialize(
        new { SchemaVersion = 1, Locale = "es-MX", Messages = messages }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static MessageCatalog Parse(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return MessageCatalog.Load(stream);
    }

    private static CatalogMessage Entry(string id, string text, string profile = "all") => new(id, [profile], "entry", text);
    private static CatalogMessage Exit(string id, string text) => new(id, ["all"], "exit", text);
    private static AttendanceEvent Mark(Employee employee, AttendanceAction action = AttendanceAction.Entry) =>
        new(Guid.NewGuid(), employee.Id, action, DateTimeOffset.UnixEpoch);

    private static void Check<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, found {actual}.");
    }

    private sealed class LocalGateway : IAttendanceGateway
    {
        public Task<AttendanceResult> SubmitAsync(AttendanceEvent attendance, CancellationToken cancellation) =>
            Task.FromResult(Local);
    }
}

internal sealed class TemporaryEditorialStore : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("upro-editorial-");

    public string DirectoryFor(EditorialEnvironment environment) =>
        root.CreateSubdirectory(environment.ToString()).FullName;

    public string HistoryPath(EditorialEnvironment environment = EditorialEnvironment.Laboratory) =>
        Path.Combine(DirectoryFor(environment), "history.json");

    public EditorialMessages Service(MessageCatalog catalog, Action<EditorialEnvironment, EditorialIssue>? report = null,
        Func<int, int>? choose = null) =>
        new(() => catalog, environment => new(DirectoryFor(environment)), report ?? ((_, _) => { }), choose ?? (_ => 0));

    public JsonElement[] Rows(EditorialEnvironment environment = EditorialEnvironment.Laboratory)
    {
        var path = HistoryPath(environment);
        if (!File.Exists(path))
            return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("assignments").EnumerateArray().Select(row => row.Clone()).ToArray();
    }

    public void Dispose()
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(root.Parent?.FullName, temp, StringComparison.OrdinalIgnoreCase)
            || !root.Name.StartsWith("upro-editorial-", StringComparison.Ordinal))
            throw new IOException("Unexpected editorial test directory.");
        root.Delete(recursive: true);
    }
}
