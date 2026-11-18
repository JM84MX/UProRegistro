using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using UProAttendance.Core;

namespace UProAttendance.Tests;

internal static class TerminalTests
{
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-11-18T15:00:00.1234567Z");
    private const string Origin = "https://terminal.test";
    private const string Temporary = "upc_r_test";
    private const string Credential = "upc_d_original";

    internal static (string Name, Action Test)[] All =>
    [
        ("First registration persists UUID and restart reuses origin-bound credential", () => Run(Registration)),
        ("Lost registration response retains identity for rotation", () => Run(LostRegistration)),
        ("Remote administration, disconnection and durable gallery restart", () => Run(Administration)),
        ("Gallery failure rejects commit and leaves earlier templates and audit intact", () => Run(GalleryFailure)),
        ("Online official time, active directory and administrative correction", () => Run(Online)),
        ("Prolonged offline operation preserves order, original time and manual break", () => Run(Offline)),
        ("Unknown response survives restart and reconciles by UUID", () => Run(Unknown)),
        ("Unknown response with 404 replays identical online request", () => Run(Replay)),
        ("Revocation and invalid credentials block offline capture across restarts", () => Run(Revocation)),
        ("Conflicts and invalid temporal authorization preserve evidence and isolate employees", () => Run(Conflict)),
        ("A corrected sequence retries the unchanged request after reading the jornada", () => Run(SequenceRecovery)),
        ("Older queue receipts leave later punches intact and concurrent work preserves sequence", () => Run(Concurrency)),
        ("Corrupted local state fails closed without replacement", () => Run(Corruption)),
        ("Mexico City midnight and UTC millisecond precision", Midnight)
    ];

    private static void Run(Func<Fixture, Task> test)
    {
        using var fixture = new Fixture();
        test(fixture).GetAwaiter().GetResult();
    }

    private static async Task Registration(Fixture f)
    {
        Guid stable;
        using (var terminal = f.Open())
        {
            Check(!(await terminal.AuthenticateAsync(default) is { IsActive: true }));
            await Expect<ArgumentException>(() => terminal.ConfigureAsync("http://terminal.test", "Huawei", Temporary, false, default));
            Check(terminal.Device is null && f.Requests.IsEmpty);
            await f.Register(terminal);
            stable = terminal.Device!.DeviceId;
            Check(stable != Guid.Empty && terminal.CanAdminister);
            await Expect<ArgumentException>(() => terminal.ConfigureAsync("https://other.test", "Huawei", "upc_d_rotated", true, default));
        }
        using var restarted = f.Open();
        Check(restarted.Device!.DeviceId == stable && restarted.Device.DeviceToken == Credential);
        await restarted.RefreshAsync(default);
        Check(f.Requests.Count(r => r.Path == "/registro-dispositivo") == 1);
        Check(f.Requests.First().Token is null && f.Requests.First().DeviceId is null);
        Check(f.Requests.Where(r => r.Path != "/registro-dispositivo")
            .All(r => r.Token == Credential && r.DeviceId == stable.ToString("D")));
        Check(f.Requests.All(r => r.Host == "terminal.test"));
    }

    private static async Task LostRegistration(Fixture f)
    {
        f.LoseRegistration = true;
        Guid stable;
        using (var first = f.Open())
        {
            await Expect<TerminalException>(() => f.Register(first));
            stable = first.Device!.DeviceId;
            Check(first.Device.DeviceToken is null);
        }
        using var terminal = f.Open();
        Check(terminal.Device!.DeviceId == stable && !terminal.CanAdminister);
        await Expect<TerminalException>(() => f.Register(terminal));
        f.Token = "upc_d_rotated";
        await terminal.ConfigureAsync(Origin, "Huawei", f.Token, true, default);
        Check(terminal.Device!.DeviceId == stable && terminal.Device.DeviceToken == f.Token);
        Check(f.Requests.Where(r => r.Path == "/registro-dispositivo")
            .All(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("deviceId").GetGuid() == stable));
    }

    private static async Task Administration(Fixture f)
    {
        using (var terminal = f.Open())
        {
            await f.Register(terminal);
            using var session = (await terminal.AuthenticateAsync(default))!;
            Check(session.IsActive);
            await Enroll(terminal, session, Finger.RightIndex);

            f.Admin = false;
            await terminal.RefreshAsync(default);
            Check(!session.IsActive && await terminal.AuthenticateAsync(default) is null);

            f.Admin = true;
            await terminal.RefreshAsync(default);
            using var next = (await terminal.AuthenticateAsync(default))!;
            var employee = terminal.FindEmployee("7")!;
            using var draft = new Biometrics(terminal.Gallery).Begin(employee, Finger.RightThumb, next);
            Check(await draft.AcceptAsync(new([2]), new Matcher(), default) == EnrollmentStatus.Accepted);
            f.ServerDown = true;
            Check(await draft.AcceptAsync(new([2]), new Matcher(), default) == EnrollmentStatus.Denied);
            Check(!next.IsActive && terminal.Gallery.Enrollments.Count == 1);
        }
        using var restarted = f.Open();
        Check(restarted.Gallery.Enrollments.Single().Samples.Count == 3);
        using var probe = new FingerprintTemplate([1]);
        var identified = new Biometrics(restarted.Gallery).Identify(probe, new Matcher(), default);
        Check(identified.Identity?.Employee.Id == "7");
        f.ServerDown = false;
        await restarted.RefreshAsync(default);
        f.Clock.Advance(TimeSpan.FromSeconds(5));
        await restarted.SynchronizeAsync(default);
        var audit = JsonDocument.Parse(f.Requests.Single(r => r.Path == "/auditoria").Body!).RootElement;
        Check(audit.GetProperty("tipo").GetString() == "ALTA_HUELLA");
        Check(!audit.TryGetProperty("metadata", out _) && audit.EnumerateObject().Count() == 5);
    }

    private static async Task GalleryFailure(Fixture f)
    {
        using (var terminal = f.Open())
        {
            await f.Register(terminal);
            using var session = (await terminal.AuthenticateAsync(default))!;
            await Enroll(terminal, session, Finger.RightIndex);
            using (File.Open(Path.Combine(f.Path, "gallery.upc"), FileMode.Open, FileAccess.Read, FileShare.Read))
                await Expect<IOException>(() => Enroll(terminal, session, Finger.RightThumb));
            Check(terminal.Gallery.Enrollments.Count == 1 && terminal.Gallery.Audits.Length == 1);
        }
        using var restarted = f.Open();
        Check(restarted.Gallery.Enrollments.Count == 1 && restarted.Gallery.Audits.Length == 1);
    }

    private static async Task Online(Fixture f)
    {
        using var terminal = f.Open();
        await f.Register(terminal);
        f.Official = "2026-11-18T15:00:04.001Z";
        var entry = await terminal.PunchAsync("7", Observed, default);
        Check(entry.Event?.Action == AttendanceAction.Entry && entry.Result?.Status == SyncStatus.Confirmed
            && entry.Result.OfficialTime == TerminalClock.Parse(f.Official));
        var request = ReadPunch(f.Requests.Single(r => r.Path == "/asistencia"));
        Check(request.ModoCaptura == "ONLINE" && request.AutorizacionTemporal == "original-proof"
            && request.HoraDispositivoUtc == "2026-11-18T15:00:00.123Z");
        f.Corrected = true;
        var closed = await terminal.PunchAsync("7", Observed.AddHours(1), default);
        Check(closed.Event is null && closed.Result is null);
        Check(f.Requests.Count(r => r.Path == "/asistencia") == 1);
        f.Roster = [new(7, "Lua", "Luz Díaz", "Gerente"), new(8, null, "Ana Mora", "Recepcionista")];
        await terminal.RefreshAsync(default);
        Check(terminal.FindEmployee("7") is { Position: EmployeePosition.Manager, GreetingName: "Lua" });
        Check(terminal.FindEmployee("8")?.GreetingName == "Ana Mora");
        f.Roster = [f.Roster[1]];
        await terminal.RefreshAsync(default);
        Check(terminal.FindEmployee("7") is null);
        Check((await terminal.PunchAsync("7", Observed.AddHours(2), default)).Result?.Status == SyncStatus.Unavailable);
    }

    private static async Task Offline(Fixture f)
    {
        Guid firstId;
        using (var terminal = f.Open())
        {
            await f.Register(terminal);
            await terminal.SetBreakAsync(TerminalClock.Day(Observed), true, default);
            f.ServerDown = true;
            await terminal.RefreshAsync(default);
            var entry = await terminal.PunchAsync("7", Observed, default);
            Check(entry is { Provisional: true, Result: { Status: SyncStatus.LocalPending, Operational: true } });
            firstId = entry.Event!.Id;
        }
        using (var terminal = f.Open())
        {
            Check(terminal.HasBreak(TerminalClock.Day(Observed)) && !terminal.HasBreak(TerminalClock.Day(Observed).AddDays(1)));
            f.Clock.Advance(TimeSpan.FromHours(3));
            var rest = await terminal.PunchAsync("7", Observed.AddHours(3), default);
            Check(rest.Event?.Action == AttendanceAction.BreakStart);
            var resumed = await terminal.PunchAsync("7", Observed.AddHours(4), default);
            Check(resumed.Event?.Action == AttendanceAction.BreakEnd);
            var exit = await terminal.PunchAsync("7", Observed.AddHours(5), default);
            Check(exit.Event?.Action == AttendanceAction.Exit);
            Check(f.Requests.All(r => r.Path != "/asistencia"));
        }
        using var recovered = f.Open();
        f.ServerDown = false;
        await recovered.RefreshAsync(default);
        await recovered.SynchronizeAsync(default);
        Check(f.Accepted.Count == 4);
        var sent = f.Requests.Where(r => r.Path == "/asistencia").ToArray();
        Check(sent.Select(ReadPunch).Select(x => x.Evento).SequenceEqual(["ENTRADA", "INICIO_DESCANSO", "FIN_DESCANSO", "SALIDA"]));
        Check(sent.All(r => !JsonDocument.Parse(r.Body!).RootElement.TryGetProperty("autorizacionTemporal", out _)));
        var first = ReadPunch(sent[0]);
        Check(first.EventoId == firstId && first.HoraDispositivoUtc == TerminalClock.Utc(Observed) && first.ModoCaptura == "OFFLINE");
        var audit = f.Requests.Where(r => r.Path == "/auditoria")
            .Select(r => JsonSerializer.Deserialize<AuditPayload>(r.Body!, UproCheckApi.Json)!).First();
        Check(audit.EventoId != firstId && audit.CorrelacionId == firstId && audit.EmpleadoId == 7);
        recovered.RecordAudit("LECTURA_HUELLA", "RECHAZADO", null, "NO_RECONOCIDA");
        await recovered.SynchronizeAsync(default);
        var unknown = JsonDocument.Parse(f.Requests.Last(r => r.Path == "/auditoria").Body!).RootElement;
        Check(!unknown.TryGetProperty("empleadoId", out _) && unknown.GetProperty("metadata").GetProperty("motivo").GetString() == "NO_RECONOCIDA");
    }

    private static async Task Unknown(Fixture f)
    {
        Guid id;
        using (var terminal = f.Open())
        {
            await f.Register(terminal);
            f.DropAfterCommit = true;
            var result = await terminal.PunchAsync("7", Observed, default);
            Check(result.Result?.Status == SyncStatus.LocalPending);
            id = result.Event!.Id;
        }
        using (var terminal = f.Open())
        {
            await terminal.RefreshAsync(default);
            await terminal.SynchronizeAsync(default);
        }
        using var restarted = f.Open();
        await restarted.RefreshAsync(default);
        await restarted.SynchronizeAsync(default);
        Check(f.Requests.Count(r => r.Path == "/asistencia") == 1);
        Check(f.Requests.Count(r => r.Path == $"/asistencia/{id:D}") == 1);
        Check((await restarted.PunchAsync("7", Observed.AddHours(1), default)).Event?.Action == AttendanceAction.Exit);
    }

    private static async Task Replay(Fixture f)
    {
        using (var terminal = f.Open())
        {
            await f.Register(terminal);
            f.DropBeforeCommit = true;
            Check((await terminal.PunchAsync("7", Observed, default)).Result?.Status == SyncStatus.LocalPending);
        }
        using var restarted = f.Open();
        f.Proof = "new-proof";
        f.Clock.Advance(TimeSpan.FromHours(4));
        await restarted.RefreshAsync(default);
        await restarted.SynchronizeAsync(default);
        var requests = f.Requests.Where(r => r.Path.StartsWith("/asistencia", StringComparison.Ordinal)).ToArray();
        Check(requests.Length == 3 && requests[0].Body == requests[2].Body);
        Check(requests[1].Path == $"/asistencia/{ReadPunch(requests[0]).EventoId:D}");
        Check(f.Accepted.Count == 1);
    }

    private static async Task Revocation(Fixture f)
    {
        using (var terminal = f.Open())
        {
            await f.Register(terminal);
            f.ServerDown = true;
            await terminal.RefreshAsync(default);
            Check((await terminal.PunchAsync("7", Observed, default)).Result?.Status == SyncStatus.LocalPending);
            f.ServerDown = false;
            f.Revoked = true;
            await terminal.RefreshAsync(default);
            Check(terminal.Device?.Blocked == TerminalError.Revoked);
        }
        using (var terminal = f.Open())
        {
            f.Revoked = false;
            f.Token = "upc_d_reactivated";
            f.ServerDown = true;
            Check((await terminal.PunchAsync("8", Observed, default)).Event is null);
            f.ServerDown = false;
            await terminal.ConfigureAsync(Origin, "Huawei", f.Token, true, default);
            await terminal.SynchronizeAsync(default);
            Check(f.Accepted.Count == 1);
            f.Token = "upc_d_rotated_again";
            await terminal.RefreshAsync(default);
            Check(terminal.Device?.Blocked == TerminalError.InvalidCredential);
        }
        using var restarted = f.Open();
        f.ServerDown = true;
        Check(!restarted.CanAdminister && (await restarted.PunchAsync("8", Observed, default)).Event is null);
    }

    private static async Task Conflict(Fixture f)
    {
        using var terminal = f.Open();
        await f.Register(terminal);
        f.Errors[7] = (HttpStatusCode.Conflict, "ATTENDANCE_UUID_CONFLICT");
        var blocked = await terminal.PunchAsync("7", Observed, default);
        Check(blocked.Result?.Status == SyncStatus.Unavailable && terminal.Incidents!.Contains("ATTENDANCE_UUID_CONFLICT"));
        var independent = await terminal.PunchAsync("8", Observed, default);
        Check(independent.Result?.Status == SyncStatus.Confirmed);
        f.Errors[8] = (HttpStatusCode.UnprocessableEntity, "TEMPORAL_AUTHORIZATION_INVALID");
        var invalid = await terminal.PunchAsync("8", Observed.AddHours(1), default);
        Check(invalid.Result?.Status == SyncStatus.Unavailable && terminal.Incidents!.Contains("TEMPORAL_AUTHORIZATION_INVALID"));
        var before = f.Requests.Count(r => r.Path == "/asistencia");
        await terminal.SynchronizeAsync(default);
        Check(f.Requests.Count(r => r.Path == "/asistencia") == before);
    }

    private static async Task Concurrency(Fixture f)
    {
        string firstPath;
        byte[] backup;
        using (var terminal = f.Open())
        {
            await f.Register(terminal);
            f.ServerDown = true;
            await terminal.RefreshAsync(default);
            var first = await terminal.PunchAsync("7", Observed, default);
            firstPath = Path.Combine(f.Path, $"punch-{first.Event!.Id:N}.upc");
            backup = await File.ReadAllBytesAsync(firstPath);
            f.ServerDown = false;
            f.Clock.Advance(TimeSpan.FromSeconds(15));
            await terminal.RefreshAsync(default);
            var pause = f.PauseAttendance();
            var syncing = terminal.SynchronizeAsync(default);
            await pause.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var capture = terminal.PunchAsync("7", Observed.AddHours(1), default);
            Check(!capture.IsCompleted);
            pause.Resume.SetResult();
            await Task.WhenAll(syncing, capture);
            await terminal.SynchronizeAsync(default);
            Check(f.Accepted.Count == 2);
        }
        await File.WriteAllBytesAsync(firstPath, backup);
        using var restarted = f.Open();
        await restarted.RefreshAsync(default);
        await restarted.SynchronizeAsync(default);
        Check(f.Accepted.Count == 2);
        Check((await restarted.PunchAsync("7", Observed.AddHours(2), default)).Event is null);
    }

    private static async Task SequenceRecovery(Fixture f)
    {
        using (var terminal = f.Open())
        {
            await f.Register(terminal);
            f.Errors[7] = (HttpStatusCode.Conflict, "ATTENDANCE_SEQUENCE_CONFLICT");
            Check((await terminal.PunchAsync("7", Observed, default)).Result?.Status == SyncStatus.Unavailable);
        }
        using var restarted = f.Open();
        f.Errors.Clear();
        await restarted.RefreshAsync(default);
        await restarted.SynchronizeAsync(default);
        Check(f.Accepted.Count == 1);
        var attempts = f.Requests.Where(r => r.Path == "/asistencia").ToArray();
        Check(attempts.Length == 2 && attempts[0].Body == attempts[1].Body);
        Check(restarted.Incidents is null);
    }

    private static async Task Corruption(Fixture f)
    {
        string path;
        using (var terminal = f.Open())
        {
            await f.Register(terminal);
            f.ServerDown = true;
            await terminal.RefreshAsync(default);
            var pending = await terminal.PunchAsync("7", Observed, default);
            path = Path.Combine(f.Path, $"punch-{pending.Event!.Id:N}.upc");
            await Expect<IOException>(() => Task.Run(() => f.Open().Dispose()));
        }
        var damaged = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(path, damaged);
        await Expect<IOException>(() => Task.Run(() => f.Open().Dispose()));
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(damaged));
    }

    private static void Midnight()
    {
        Check(TerminalClock.Day(TerminalClock.Parse("2026-11-18T05:59:59.999Z")) == new DateOnly(2026, 11, 17));
        Check(TerminalClock.Day(TerminalClock.Parse("2026-11-18T06:00:00.000Z")) == new DateOnly(2026, 11, 18));
        Check(TerminalClock.Day(TerminalClock.Parse("2026-07-01T05:59:59.999Z")) == new DateOnly(2026, 6, 30));
        Check(TerminalClock.Utc(Observed) == "2026-11-18T15:00:00.123Z");
    }

    private static async Task Enroll(OperationalTerminal terminal, IEnrollmentSession session, Finger finger)
    {
        using var draft = new Biometrics(terminal.Gallery).Begin(terminal.FindEmployee("7")!, finger, session);
        var marker = finger == Finger.RightIndex ? (byte)1 : (byte)2;
        for (var i = 0; i < Biometrics.SampleCount; i++)
            Check(await draft.AcceptAsync(new([marker]), new Matcher(), default) ==
                (i == Biometrics.SampleCount - 1 ? EnrollmentStatus.Completed : EnrollmentStatus.Accepted));
    }

    private static void Check(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? detail = null)
    {
        if (!condition)
            throw new InvalidOperationException(detail);
    }

    private static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static PunchPayload ReadPunch(Request request) =>
        JsonSerializer.Deserialize<PunchPayload>(request.Body!, UproCheckApi.Json)!;

    private sealed class Matcher : IFingerprintMatcher
    {
        public bool Matches(FingerprintTemplate left, FingerprintTemplate right, MatchLevel level) =>
            left.Bytes.SequenceEqual(right.Bytes);
    }

    private sealed record Request(string Path, string Host, string? Body, string? Token, string? DeviceId);
    private sealed record Pause(TaskCompletionSource Started, TaskCompletionSource Resume);
    private sealed class Fixture : HttpMessageHandler
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "upc-test-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient client;
        private Guid registered;
        private Pause? pause;
        public ConcurrentQueue<Request> Requests { get; } = new();
        public Dictionary<Guid, (PunchPayload Payload, Confirmation Confirmation)> Accepted { get; } = [];
        public Dictionary<int, (HttpStatusCode Status, string Code)> Errors { get; } = [];
        public FakeClock Clock { get; } = new();
        public RemoteEmployee[] Roster { get; set; } = [new(7, "Luz", "Luz Díaz", "Estilista"), new(8, null, "Ana Mora", "Recepcionista")];
        public bool Admin { get; set; } = true;
        public bool LoseRegistration { get; set; }
        public bool DropAfterCommit { get; set; }
        public bool DropBeforeCommit { get; set; }
        public bool ServerDown { get; set; }
        public bool Revoked { get; set; }
        public bool Corrected { get; set; }
        public string Token { get; set; } = Credential;
        public string Proof { get; set; } = "original-proof";
        public string? Official { get; set; }

        public Fixture() => client = new(this, disposeHandler: false);
        public OperationalTerminal Open() => new(Path, new(client), Clock);
        public Task Register(OperationalTerminal terminal) => terminal.ConfigureAsync(Origin, "Huawei", Temporary, false, default);

        public Pause PauseAttendance()
        {
            pause = new(new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
            return pause;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
        {
            var uri = message.RequestUri!;
            var route = uri.AbsolutePath["/api/uprocheck".Length..];
            var body = message.Content is null ? null : await message.Content.ReadAsStringAsync(ct);
            var header = message.Headers.TryGetValues("X-UPro-Device-Id", out var values) ? values.Single() : null;
            var request = new Request(route, uri.Host, body, message.Headers.Authorization?.Parameter, header);
            Requests.Enqueue(request);
            if (route == "/asistencia" && Interlocked.Exchange(ref pause, null) is { } pending)
            {
                pending.Started.SetResult();
                await pending.Resume.Task.WaitAsync(ct);
            }
            lock (Accepted)
            {
                if (ServerDown)
                    return Error(HttpStatusCode.ServiceUnavailable, "SERVICE_UNAVAILABLE");
                if (route == "/registro-dispositivo")
                {
                    var registration = JsonSerializer.Deserialize<Registration>(body!, UproCheckApi.Json)!;
                    Check(registration.Token == Temporary && request.Token is null && header is null);
                    if (registered != Guid.Empty)
                        return Error(HttpStatusCode.Conflict, "DEVICE_ALREADY_REGISTERED");
                    registered = registration.DeviceId;
                    if (LoseRegistration)
                        throw new HttpRequestException("Lost response");
                    return Reply(new Credential(registered, 1, Token), HttpStatusCode.Created);
                }
                if (request.Token != Token || header != registered.ToString("D"))
                    return Error(HttpStatusCode.Unauthorized, "DEVICE_UNAUTHENTICATED");
                if (Revoked)
                    return Error(HttpStatusCode.Forbidden, "DEVICE_REVOKED");

                switch (route)
                {
                    case "/heartbeat":
                        return Reply(new Heartbeat(true, TerminalClock.Utc(Clock.GetUtcNow()), "America/Mexico_City", new(Admin),
                            new(Proof, TerminalClock.Utc(Clock.GetUtcNow().AddMinutes(2)))));
                    case "/empleados":
                        return Reply(new DirectoryResponse(Roster));
                    case "/enrolamiento/autorizacion":
                        var employee = JsonSerializer.Deserialize<EnrollmentRequest>(body!, UproCheckApi.Json)!;
                        return Admin ? Reply(new EnrollmentResponse(true, employee.EmpleadoId))
                            : Error(HttpStatusCode.Forbidden, "ENROLLMENT_DISABLED");
                    case "/jornada":
                        var query = uri.Query.Split('&');
                        var id = int.Parse(query[0].Split('=')[1]);
                        var day = DateOnly.ParseExact(query[1].Split('=')[1], "yyyy-MM-dd");
                        return Reply(GetDay(id, day));
                    case "/asistencia":
                        return Attend(ReadPunch(request));
                    case "/auditoria":
                        var audit = JsonSerializer.Deserialize<AuditPayload>(body!, UproCheckApi.Json)!;
                        return Reply(new AuditReceipt(true, audit.EventoId));
                    default:
                        var uuid = Guid.Parse(route["/asistencia/".Length..]);
                        return Accepted.TryGetValue(uuid, out var existing) ? Reply(existing.Confirmation)
                            : Error(HttpStatusCode.NotFound, "ATTENDANCE_NOT_FOUND");
                }
            }
        }

        private RemoteDay GetDay(int id, DateOnly day)
        {
            var moves = Accepted.Values.Where(x => x.Payload.EmpleadoId == id && x.Confirmation.JornadaFecha == day)
                .Select(x => new RemoteMovement(x.Payload.Evento, x.Confirmation.HoraEventoUtc, x.Confirmation.FuenteTemporal)).ToList();
            if (Corrected && id == 7)
                moves.Add(new("SALIDA", TerminalClock.Utc(Observed.AddHours(1)), "CORRECCION"));
            string[] next = moves.LastOrDefault()?.Evento switch
            {
                "ENTRADA" => ["SALIDA", "INICIO_DESCANSO"],
                "INICIO_DESCANSO" => ["FIN_DESCANSO"],
                "FIN_DESCANSO" => ["SALIDA"],
                "SALIDA" => [],
                _ => ["ENTRADA"]
            };
            return new(id, day, next.Length == 0 ? "CERRADA" : "ABIERTA", next, [.. moves]);
        }

        private HttpResponseMessage Attend(PunchPayload payload)
        {
            if (DropBeforeCommit)
            {
                DropBeforeCommit = false;
                throw new HttpRequestException("Request lost");
            }
            if (Accepted.TryGetValue(payload.EventoId, out var previous))
                return previous.Payload == payload ? Reply(previous.Confirmation) : Error(HttpStatusCode.Conflict, "ATTENDANCE_UUID_CONFLICT");
            if (Errors.TryGetValue(payload.EmpleadoId, out var error))
                return Error(error.Status, error.Code);
            var utc = payload.ModoCaptura == "ONLINE" ? Official ?? payload.HoraDispositivoUtc : payload.HoraDispositivoUtc;
            var result = new Confirmation(true, "accepted", payload.EventoId, utc, TerminalClock.Day(TerminalClock.Parse(utc)),
                payload.ModoCaptura == "ONLINE" ? "SERVIDOR" : "DISPOSITIVO_OFFLINE");
            Accepted.Add(payload.EventoId, (payload, result));
            if (DropAfterCommit)
            {
                DropAfterCommit = false;
                throw new HttpRequestException("Response lost");
            }
            return Reply(result);
        }

        private static HttpResponseMessage Reply<T>(T data, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(data, options: UproCheckApi.Json) };
        private static HttpResponseMessage Error(HttpStatusCode status, string code) => Reply(new { error = code, code }, status);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                client.Dispose();
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            base.Dispose(disposing);
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => Observed.AddTicks(Interlocked.Read(ref ticks));
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }
}
