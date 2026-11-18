using System.IO;
using UProAttendance.Core;

namespace UProAttendance;

internal enum QueueState { New, Unknown, Confirmed, Conflict, Blocked }
internal sealed record StoredPunch(PunchPayload Payload, AuditPayload Audit, DateOnly Day, long Order,
    QueueState State = QueueState.New, Confirmation? Confirmation = null, string? Issue = null);
internal sealed record DaySnapshot(RemoteDay Day, Guid[] Confirmed);
internal sealed record AuditState(AuditPayload Payload, bool Complete = false, string? Issue = null);

internal sealed class OperationalTerminal : IEnrollmentAuthorization, IEmployeeProvider, IAppointmentHoursProvider, IDisposable
{
    private readonly TerminalFiles files;
    private readonly UproCheckApi api;
    private readonly object gate = new();
    private readonly SemaphoreSlim refresh = new(1);
    private readonly SemaphoreSlim synchronization = new(1);
    private readonly SemaphoreSlim captures = new(1);
    private readonly Dictionary<Guid, StoredPunch> punches = [];
    private readonly Dictionary<Guid, AuditState> audits = [];
    private readonly Dictionary<(int, DateOnly), DaySnapshot> days = [];
    private readonly Dictionary<Guid, long> conflictChecks = [];
    private readonly Dictionary<DateOnly, bool> breaks;
    private Employee[] directory;
    private DeviceIdentity? device;
    private Heartbeat? heartbeat;
    private readonly TimeProvider clock;
    private long heartbeatAt;
    private TimeSpan onlineWindow;
    private TerminalError? connection;
    private bool storageFailed;
    private long order;
    private long privilegeEpoch;
    private TimeSpan retryDelay = TimeSpan.Zero;
    private long retryAt;

    public PersistentTemplateGallery Gallery { get; }
    public event Action? AccessChanged;
    public DeviceIdentity? Device { get { lock (gate) return device; } }
    public bool CanAdminister { get { lock (gate) return CanAdministerLocked(); } }
    public string? Incidents
    {
        get
        {
            lock (gate)
            {
                var items = punches.Values.Where(x => x.Issue is not null)
                    .Select(x => $"{x.Payload.EventoId:D} · {x.Issue}")
                    .Concat(audits.Values.Where(x => x.Issue is not null).Select(x => $"{x.Payload.EventoId:D} · {x.Issue}"))
                    .Take(8).ToArray();
                return items.Length == 0 ? null : string.Join(Environment.NewLine, items);
            }
        }
    }
    public string Status
    {
        get
        {
            lock (gate)
            {
                if (storageFailed)
                    return "Almacenamiento no disponible";
                if (device?.Blocked == TerminalError.Revoked)
                    return "Terminal revocada";
                if (device?.Blocked == TerminalError.InvalidCredential)
                    return "Credencial inválida";
                if (device?.DeviceToken is null)
                    return "Terminal sin configurar";

                var status = connection switch
                {
                    TerminalError.Offline => "Sin red",
                    TerminalError.Server => "Servidor no disponible",
                    TerminalError.Contract => "Error de integración",
                    _ => IsOnlineLocked() ? "Conectada" : "Conexión sin verificar"
                };
                var pending = punches.Values.Count(x => x.State is QueueState.New or QueueState.Unknown);
                var incidents = punches.Values.Count(x => x.State is QueueState.Blocked or QueueState.Conflict)
                    + audits.Values.Count(x => x.Issue is not null);
                var provisional = punches.Values.Any(x => x.State is QueueState.New or QueueState.Unknown
                    && !days.ContainsKey((x.Payload.EmpleadoId, x.Day)));
                if (pending > 0)
                    status += provisional ? $" · {pending} pendientes (jornada provisional)" : $" · {pending} pendientes";
                return incidents == 0 ? status : $"{status} · {incidents} incidencias";
            }
        }
    }

    public OperationalTerminal(string folder, UproCheckApi api, TimeProvider? clock = null)
    {
        this.api = api;
        this.clock = clock ?? TimeProvider.System;
        files = new(folder);
        try
        {
            device = files.Read<DeviceIdentity>("device");
            if (device is not null && (device.DeviceId == Guid.Empty || device.Url is null
                || UproCheckApi.ValidateUrl(device.Url.AbsoluteUri) != device.Url
                || device.DeviceToken is not null && !ValidToken(device.DeviceToken)))
                throw new IOException("Identidad local inválida.");
            directory = files.Read<Employee[]>("employees") ?? [];
            ValidateDirectory(directory);
            breaks = files.Read<Dictionary<DateOnly, bool>>("breaks") ?? [];
            foreach (var name in files.Names("punch-"))
            {
                var item = files.Read<StoredPunch>(name) ?? throw new IOException("Marcaje local ausente.");
                if (item.Payload is null || item.Audit is null || item.Payload.EventoId == Guid.Empty
                    || name != PunchFile(item.Payload.EventoId) || item.Payload.EmpleadoId <= 0
                    || item.Order <= 0 || !Enum.IsDefined(item.State) || item.Audit.CorrelacionId != item.Payload.EventoId
                    || item.State == QueueState.Confirmed && item.Confirmation is null
                    || item.Payload.ModoCaptura is not ("ONLINE" or "OFFLINE")
                    || (item.Payload.ModoCaptura == "ONLINE") != (item.Payload.AutorizacionTemporal is not null)
                    || TerminalClock.Day(TerminalClock.Parse(item.Payload.HoraDispositivoUtc)) != item.Day)
                    throw new IOException("Marcaje local inválido.");
                ParseEvent(item.Payload.Evento);
                if (item.Confirmation is { } confirmation)
                    ValidateConfirmation(confirmation, item.Payload.EventoId);
                ValidateAudit(item.Audit);
                punches.Add(item.Payload.EventoId, item);
                order = Math.Max(order, item.Order);
            }
            foreach (var name in files.Names("audit-"))
            {
                var item = files.Read<AuditState>(name) ?? throw new IOException("Auditoría local ausente.");
                ValidateAudit(item.Payload);
                if (name != AuditFile(item.Payload.EventoId))
                    throw new IOException("Identidad de auditoría local inválida.");
                audits.Add(item.Payload.EventoId, item);
            }
            foreach (var name in files.Names("day-"))
            {
                var snapshot = files.Read<DaySnapshot>(name) ?? throw new IOException("Jornada local ausente.");
                if (snapshot.Day is not { EmpleadoId: > 0 } day || snapshot.Confirmed is null
                    || name != DayFile(day.EmpleadoId, day.JornadaFecha))
                    throw new IOException("Jornada local inválida.");
                ValidateDay(day, day.EmpleadoId, day.JornadaFecha);
                days.Add((day.EmpleadoId, day.JornadaFecha), snapshot);
            }
            Gallery = new(files);
            if (device is null && (punches.Count != 0 || directory.Length != 0 || Gallery.Enrollments.Count != 0
                                   || audits.Count != 0 || days.Count != 0 || breaks.Count != 0))
            {
                Gallery.Dispose();
                throw new IOException("Falta la identidad local de esta terminal.");
            }
        }
        catch
        {
            files.Dispose();
            throw;
        }
    }

    public async Task ConfigureAsync(string url, string name, string secret, bool replacement, CancellationToken ct)
    {
        await refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var origin = UproCheckApi.ValidateUrl(url);
            name = name.Trim();
            DeviceIdentity identity;
            lock (gate)
            {
                if (device is { } existing)
                {
                    if (existing.Url != origin)
                        throw new ArgumentException("El origen está vinculado a esta instalación.");
                    if (replacement == false && existing.DeviceToken is not null)
                        throw new InvalidOperationException("La terminal ya tiene una identidad registrada.");
                    identity = existing;
                }
                else
                {
                    if (replacement || name.Length is < 1 or > 100)
                        throw new ArgumentException("Introduce un nombre de 1 a 100 caracteres para el autorregistro.");
                    identity = new(origin, Guid.NewGuid(), name, null, 0);
                    SaveDevice(identity);
                }
            }

            if (replacement)
            {
                if (!ValidToken(secret))
                    throw new ArgumentException("Introduce la credencial individual emitida por UProApp.");
                var candidate = identity with { DeviceToken = secret, Blocked = null, VersionCredencial = 0 };
                var response = await api.Heartbeat(candidate, ct).ConfigureAwait(false);
                lock (gate)
                {
                    ValidateHeartbeat(response);
                    SaveDevice(candidate);
                    ClearHeartbeat();
                }
            }
            else
            {
                var credential = await api.Register(identity, secret, ct).ConfigureAwait(false);
                if (credential.DeviceId != identity.DeviceId || credential.VersionCredencial != 1
                    || !ValidToken(credential.DeviceToken))
                    throw new TerminalException(TerminalError.Contract, "INVALID_REGISTRATION");
                lock (gate)
                    SaveDevice(identity with { DeviceToken = credential.DeviceToken, VersionCredencial = credential.VersionCredencial });
            }
        }
        finally { refresh.Release(); }

        // Registration persists the one-time response before any follow-up call.
        await RefreshAsync(ct).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (UsableDevice() is not { } identity)
                return;
            try
            {
                var started = clock.GetTimestamp();
                var result = await api.Heartbeat(identity, ct).ConfigureAwait(false);
                ValidateHeartbeat(result);
                if (!result.Configuration.RegistroHuellaAdmon)
                {
                    lock (gate)
                        if (heartbeat?.Configuration.RegistroHuellaAdmon == true)
                            ClearHeartbeat();
                }
                var response = await api.Employees(identity, ct).ConfigureAwait(false);
                var roster = (response.Empleados ?? throw Protocol("INVALID_DIRECTORY")).Select(UproCheckApi.Map).ToArray();
                ValidateDirectory(roster);
                lock (gate)
                {
                    if (device != identity || storageFailed)
                        return;
                    if (!directory.SequenceEqual(roster))
                    {
                        Write("employees", roster);
                        directory = roster;
                    }
                    ApplyHeartbeat(result, started);
                }
            }
            catch (TerminalException error) { Handle(identity, error); }
        }
        finally { refresh.Release(); }
    }

    public async Task RunAsync(Action update, CancellationToken ct)
    {
        await Task.WhenAll(RefreshLoop(update, ct), SynchronizationLoop(update, ct)).ConfigureAwait(false);
    }

    private async Task RefreshLoop(Action update, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        do
        {
            try { await RefreshAsync(ct).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            update();
        } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task SynchronizationLoop(Action update, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3), clock);
        do
        {
            try { await SynchronizeAsync(ct).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            update();
        } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    public Task<TimeSpan?> GetTotalAsync(DateOnly day, CancellationToken cancellation) => Task.FromResult<TimeSpan?>(null);

    public bool HasBreak(DateOnly day) { lock (gate) return breaks.GetValueOrDefault(day); }

    public async Task SetBreakAsync(DateOnly day, bool enabled, CancellationToken ct)
    {
        await RefreshAsync(ct).ConfigureAwait(false);
        lock (gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!CanAdministerLocked())
                throw new UnauthorizedAccessException();
            var updated = new Dictionary<DateOnly, bool>(breaks) { [day] = enabled };
            Write("breaks", updated);
            breaks[day] = enabled;
        }
    }

    public Employee? FindEmployee(string id)
    {
        lock (gate)
            return directory.FirstOrDefault(employee => employee.Id == id);
    }

    public Task<IEnrollmentSession?> AuthenticateAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult<IEnrollmentSession?>(CanAdministerLocked() ? new Session(this, privilegeEpoch) : null);
    }

    public Task<IReadOnlyList<Employee>> GetEmployeesAsync(IEnrollmentSession session, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (session is not Session { IsActive: true } operational || operational.Owner != this)
                throw new UnauthorizedAccessException();
            return Task.FromResult<IReadOnlyList<Employee>>(directory);
        }
    }

    private async Task<bool> Authorize(Session session, string employee, CancellationToken ct)
    {
        if (!session.IsActive || UsableDevice(online: true) is not { } identity)
            return false;

        try
        {
            var id = UproCheckApi.EmployeeId(employee);
            var result = await api.Authorize(identity, id, ct).ConfigureAwait(false);
            return session.IsActive && result.Authorized && result.EmpleadoId == id;
        }
        catch (TerminalException error)
        {
            Handle(identity, error);
            return false;
        }
    }

    public void RecordAudit(string type, string result, string? employeeId, string? reason)
    {
        lock (gate)
        {
            if (device?.DeviceToken is null || storageFailed)
                return;
            var payload = MakeAudit(type, result, employeeId is null ? null : UproCheckApi.EmployeeId(employeeId), reason);
            StoreAudit(new(payload));
        }
    }

    public async Task<DailyPunch> PunchAsync(string employeeId, DateTimeOffset observedAt, CancellationToken ct)
    {
        await captures.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = UproCheckApi.EmployeeId(employeeId);
            var day = TerminalClock.Day(observedAt);
            DeviceIdentity? identity;
            string? proof;
            lock (gate)
            {
                identity = UsableDevice();
                proof = IsOnlineLocked() ? heartbeat!.AutorizacionTemporal.Token : null;
            }
            if (identity is null || FindEmployee(employeeId) is null)
            {
                RecordAudit("LECTURA_HUELLA", "EXITOSO", employeeId, null);
                return Unavailable();
            }

            if (proof is not null)
            {
                await synchronization.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await ConfirmUnknownForEmployee(identity, id, ct).ConfigureAwait(false);
                    if (!HasUnknownForEmployee(id) && UsableDevice(online: true) == identity)
                        await UpdateDay(identity, id, day, ct).ConfigureAwait(false);
                }
                finally { synchronization.Release(); }
            }

            AttendanceEvent request;
            StoredPunch item;
            lock (gate)
            {
                if (UsableDevice() != identity || FindEmployee(employeeId) is null)
                {
                    RecordAudit("LECTURA_HUELLA", "EXITOSO", employeeId, null);
                    return Unavailable();
                }

                var action = Next(id, day, out var blocked);
                if (action is null)
                {
                    StoreAudit(new(MakeAudit("LECTURA_HUELLA", "EXITOSO", id)));
                    return blocked ? Unavailable() : new(null, null, null);
                }

                // Keep the authorization observed at capture even when a subsequent request loses connectivity.
                request = new(Guid.NewGuid(), employeeId, action.Value, observedAt);
                var payload = new PunchPayload(request.Id, id, UproCheckApi.EventName(request.Action),
                    TerminalClock.Utc(observedAt), proof is null ? "OFFLINE" : "ONLINE", proof);
                item = new(payload, MakeAudit("LECTURA_HUELLA", "EXITOSO", id, correlation: request.Id), day, ++order);
                StorePunch(item);
            }

            if (proof is not null && await synchronization.WaitAsync(0, ct).ConfigureAwait(false))
            {
                try
                {
                    bool first;
                    lock (gate)
                        first = !punches.Values.Any(x => x.Payload.EmpleadoId == id && x.Order < item.Order
                            && x.State != QueueState.Confirmed);
                    if (first && UsableDevice(online: true) == identity)
                        await Deliver(identity, item, ct).ConfigureAwait(false);
                }
                finally { synchronization.Release(); }
            }

            lock (gate)
            {
                item = punches[request.Id];
                var result = item.State switch
                {
                    QueueState.Confirmed => new AttendanceResult(SyncStatus.Confirmed,
                        TerminalClock.Parse(item.Confirmation!.HoraEventoUtc)),
                    QueueState.Blocked or QueueState.Conflict => new AttendanceResult(SyncStatus.Unavailable),
                    _ when device?.Blocked is not null => new AttendanceResult(SyncStatus.Unavailable),
                    _ => new AttendanceResult(SyncStatus.LocalPending) { Operational = true }
                };
                return new(request, result, Next(id, day, out _))
                {
                    Provisional = item.State != QueueState.Confirmed && !days.ContainsKey((id, day))
                };
            }
        }
        finally { captures.Release(); }
    }

    private static DailyPunch Unavailable() => new(null, new(SyncStatus.Unavailable), null);

    internal async Task SynchronizeAsync(CancellationToken ct)
    {
        if (!await synchronization.WaitAsync(0, ct).ConfigureAwait(false))
            return;
        try
        {
            if (UsableDevice(online: true) is not { } identity
                || retryDelay > TimeSpan.Zero && clock.GetElapsedTime(retryAt) < retryDelay)
                return;

            StoredPunch[] snapshot;
            lock (gate) snapshot = punches.Values.OrderBy(x => x.Order).ToArray();
            foreach (var group in snapshot.GroupBy(x => x.Payload.EmpleadoId))
            {
                foreach (var item in group.Where(x => x.State != QueueState.Confirmed))
                {
                    ct.ThrowIfCancellationRequested();
                    if (captures.CurrentCount == 0 || UsableDevice(online: true) != identity)
                        return;
                    if (item.State == QueueState.Conflict)
                    {
                        if (!await ReconcileSequence(identity, item, ct).ConfigureAwait(false))
                            break;
                        continue;
                    }
                    if (item.State == QueueState.Blocked)
                        break;
                    if (!await Deliver(identity, item, ct).ConfigureAwait(false))
                        break;
                }
            }

            AuditPayload[] pending;
            lock (gate)
            {
                pending = punches.Values.OrderBy(x => x.Order).Select(x => x.Audit).Concat(Gallery.Audits)
                    .Concat(audits.Values.Where(x => !x.Complete && x.Issue is null).Select(x => x.Payload))
                    .DistinctBy(x => x.EventoId).Where(x => !audits.TryGetValue(x.EventoId, out var state)
                        || !state.Complete && state.Issue is null).ToArray();
            }
            foreach (var audit in pending)
            {
                if (captures.CurrentCount == 0 || UsableDevice(online: true) != identity)
                    return;
                try
                {
                    var receipt = await api.Audit(identity, audit, ct).ConfigureAwait(false);
                    if (!receipt.Accepted || receipt.EventoId != audit.EventoId)
                        throw Protocol("INVALID_AUDIT_RECEIPT");
                    lock (gate) StoreAudit(new(audit, Complete: true));
                }
                catch (TerminalException error)
                {
                    Handle(identity, error);
                    if (error.Transient || IsAuthentication(error))
                        return;
                    lock (gate) StoreAudit(new(audit, Issue: error.Message));
                }
            }
            retryDelay = TimeSpan.Zero;
        }
        finally { synchronization.Release(); }
    }

    private async Task ConfirmUnknownForEmployee(DeviceIdentity identity, int employee, CancellationToken ct)
    {
        StoredPunch[] snapshot;
        lock (gate) snapshot = punches.Values.Where(x => x.Payload.EmpleadoId == employee && x.State == QueueState.Unknown)
            .OrderBy(x => x.Order).ToArray();
        foreach (var item in snapshot)
            if (!await Deliver(identity, item, ct).ConfigureAwait(false))
                return;
    }

    private bool HasUnknownForEmployee(int employee)
    {
        lock (gate) return punches.Values.Any(x => x.Payload.EmpleadoId == employee && x.State == QueueState.Unknown);
    }

    private async Task<bool> ReconcileSequence(DeviceIdentity identity, StoredPunch item, CancellationToken ct)
    {
        if (item.Issue != "ATTENDANCE_SEQUENCE_CONFLICT"
            || conflictChecks.TryGetValue(item.Payload.EventoId, out var previous)
                && clock.GetElapsedTime(previous) < TimeSpan.FromMinutes(1))
            return false;
        conflictChecks[item.Payload.EventoId] = clock.GetTimestamp();
        var remote = await UpdateDay(identity, item.Payload.EmpleadoId, item.Day, ct).ConfigureAwait(false);
        return remote?.SiguientesEventos.Contains(item.Payload.Evento) == true
            && await Deliver(identity, item with { State = QueueState.Unknown }, ct).ConfigureAwait(false);
    }

    private async Task<bool> Deliver(DeviceIdentity identity, StoredPunch item, CancellationToken ct)
    {
        var submitted = false;
        try
        {
            Confirmation? confirmation = null;
            if (item.State == QueueState.Unknown)
            {
                try { confirmation = await api.Confirm(identity, item.Payload.EventoId, ct).ConfigureAwait(false); }
                catch (TerminalException error) when (error.Kind == TerminalError.Missing && error.Message == "ATTENDANCE_NOT_FOUND") { }
            }
            if (confirmation is null)
            {
                lock (gate) StorePunch(item with { State = QueueState.Unknown });
                submitted = true;
                confirmation = await api.Submit(identity, item.Payload, ct).ConfigureAwait(false);
            }
            ValidateConfirmation(confirmation, item.Payload.EventoId);
            lock (gate) StorePunch(item with { State = QueueState.Confirmed, Confirmation = confirmation, Issue = null });
            return true;
        }
        catch (TerminalException error)
        {
            if (error.Transient || IsAuthentication(error))
            {
                Handle(identity, error);
                return false;
            }
            if (submitted && error.Message is "INVALID_JSON" or "EMPTY_RESPONSE" or "INVALID_CONFIRMATION" or "INVALID_UTC")
                return false; // The POST may have committed. Resolve its UUID on the next pass.
            lock (gate) StorePunch(item with
            {
                State = error.Kind == TerminalError.Conflict ? QueueState.Conflict : QueueState.Blocked,
                Issue = error.Message
            });
            return false;
        }
    }

    private async Task<RemoteDay?> UpdateDay(DeviceIdentity identity, int employee, DateOnly day, CancellationToken ct)
    {
        try
        {
            var remote = await api.Day(identity, employee, day, ct).ConfigureAwait(false);
            ValidateDay(remote, employee, day);
            lock (gate)
            {
                var snapshot = new DaySnapshot(remote, punches.Values.Where(x => x.Payload.EmpleadoId == employee
                    && x.State == QueueState.Confirmed).Select(x => x.Payload.EventoId).ToArray());
                Write(DayFile(employee, day), snapshot);
                days[(employee, day)] = snapshot;
            }
            return remote;
        }
        catch (TerminalException error)
        {
            Handle(identity, error);
            return null;
        }
    }

    private AttendanceAction? Next(int employee, DateOnly day, out bool blocked)
    {
        var snapshot = days.GetValueOrDefault((employee, day));
        var local = punches.Values.Where(x => x.Payload.EmpleadoId == employee
            && (x.Confirmation?.JornadaFecha ?? x.Day) == day).OrderBy(x => x.Order).ToArray();
        blocked = snapshot?.Day.Estado == "INCOMPLETA" || local.Any(x => x.State is QueueState.Conflict or QueueState.Blocked);
        if (blocked || snapshot?.Day.Estado == "CERRADA")
            return null;

        var extra = local.Where(x => snapshot?.Confirmed.Contains(x.Payload.EventoId) != true).ToArray();
        var next = snapshot?.Day.SiguientesEventos.Select(ParseEvent).ToArray() ?? [AttendanceAction.Entry];
        foreach (var item in extra)
        {
            var action = ParseEvent(item.Payload.Evento);
            if (!next.Contains(action))
            {
                blocked = true;
                return null;
            }
            next = Successors(action);
        }

        if (next.Length == 2)
            return HasBreak(day) ? AttendanceAction.BreakStart : AttendanceAction.Exit;
        return next.Length == 0 ? null : next[0];
    }

    private static AttendanceAction[] Successors(AttendanceAction action) => action switch
    {
        AttendanceAction.Entry => [AttendanceAction.BreakStart, AttendanceAction.Exit],
        AttendanceAction.BreakStart => [AttendanceAction.BreakEnd],
        AttendanceAction.BreakEnd => [AttendanceAction.Exit],
        _ => []
    };

    private AuditPayload MakeAudit(string type, string result, int? employee, string? reason = null, Guid? correlation = null) =>
        new(Guid.NewGuid(), type, result, TerminalClock.Utc(clock.GetUtcNow()), employee, correlation,
            reason is null ? null : new(reason));

    private static string AuditFile(Guid id) => $"audit-{id:N}";
    private static string PunchFile(Guid id) => $"punch-{id:N}";
    private static string DayFile(int employee, DateOnly day) => FormattableString.Invariant($"day-{employee}-{day:yyyy-MM-dd}");

    private void StoreAudit(AuditState state)
    {
        Write(AuditFile(state.Payload.EventoId), state);
        audits[state.Payload.EventoId] = state;
    }

    private void StorePunch(StoredPunch item)
    {
        Write(PunchFile(item.Payload.EventoId), item);
        punches[item.Payload.EventoId] = item;
    }

    private void SaveDevice(DeviceIdentity identity)
    {
        Write("device", identity);
        device = identity;
    }

    private void Write<T>(string file, T data) where T : class
    {
        if (storageFailed)
            throw new IOException("Almacenamiento local bloqueado.");
        try { files.Write(file, data); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            storageFailed = true;
            ClearHeartbeat();
            throw;
        }
    }

    private DeviceIdentity? UsableDevice(bool online = false)
    {
        lock (gate)
            return !storageFailed && (!online || IsOnlineLocked()) && device is { DeviceToken: not null, Blocked: null }
                ? device : null;
    }

    private bool IsOnlineLocked() => heartbeat is not null && clock.GetElapsedTime(heartbeatAt) < onlineWindow;
    private bool CanAdministerLocked() => !storageFailed && IsOnlineLocked() && heartbeat!.Configuration.RegistroHuellaAdmon;

    private void ApplyHeartbeat(Heartbeat response, long started)
    {
        if (!IsOnlineLocked() || heartbeat?.Configuration.RegistroHuellaAdmon == true && !response.Configuration.RegistroHuellaAdmon)
            privilegeEpoch++;
        heartbeatAt = started;
        onlineWindow = TerminalClock.Parse(response.AutorizacionTemporal.ExpiraEnUtc) - TerminalClock.Parse(response.ServerTimeUtc);
        // Allow for request latency and detect a missing periodic refresh before the proof expires.
        onlineWindow = TimeSpan.FromSeconds(Math.Min(40, onlineWindow.TotalSeconds - 8));
        heartbeat = response;
        connection = null;
        AccessChanged?.Invoke();
    }

    private void ClearHeartbeat()
    {
        heartbeat = null;
        privilegeEpoch++;
        AccessChanged?.Invoke();
    }

    private void Handle(DeviceIdentity identity, TerminalException error)
    {
        lock (gate)
        {
            if (device != identity)
                return;
            if (error.Transient)
            {
                retryAt = clock.GetTimestamp();
                retryDelay = TimeSpan.FromSeconds(Math.Min(300, Math.Max(2, retryDelay.TotalSeconds * 2)));
            }
            if (error.Transient || IsAuthentication(error) || error.Kind is TerminalError.Contract or TerminalError.Denied)
            {
                connection = error.Kind;
                ClearHeartbeat();
            }
            if (IsAuthentication(error))
            {
                // Latch before attempting persistence so this process still fails closed on I/O errors.
                device = identity with { Blocked = error.Kind };
                SaveDevice(device);
            }
        }
    }

    private static bool IsAuthentication(TerminalException error) =>
        error.Kind is TerminalError.InvalidCredential or TerminalError.Revoked;

    private static bool ValidToken(string? token) => token is not null && token.StartsWith("upc_d_", StringComparison.Ordinal)
        && token.Length > 6 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static TerminalException Protocol(string code) => new(TerminalError.Contract, code);

    private static void ValidateHeartbeat(Heartbeat value)
    {
        if (!value.Authenticated || value.Configuration is null || value.BusinessTimeZone != "America/Mexico_City"
            || value.AutorizacionTemporal is not { Token.Length: > 0 } proof
            || TerminalClock.Parse(proof.ExpiraEnUtc) - TerminalClock.Parse(value.ServerTimeUtc)
                is var window && (window <= TimeSpan.Zero || window > TimeSpan.FromMinutes(2)))
            throw Protocol("INVALID_HEARTBEAT");
    }

    private static void ValidateDirectory(Employee[] roster)
    {
        if (roster.Any(employee => employee is null || string.IsNullOrWhiteSpace(employee.Name)
            || UproCheckApi.EmployeeId(employee.Id) <= 0 || !Enum.IsDefined(employee.Position))
            || roster.DistinctBy(employee => employee.Id).Count() != roster.Length)
            throw Protocol("INVALID_DIRECTORY");
    }

    private static AttendanceAction ParseEvent(string value) => value switch
    {
        "ENTRADA" => AttendanceAction.Entry,
        "INICIO_DESCANSO" => AttendanceAction.BreakStart,
        "FIN_DESCANSO" => AttendanceAction.BreakEnd,
        "SALIDA" => AttendanceAction.Exit,
        _ => throw Protocol("INVALID_EVENT")
    };

    private static void ValidateDay(RemoteDay day, int employee, DateOnly date)
    {
        if (day.EmpleadoId != employee || day.JornadaFecha != date || day.Movimientos is null
            || day.SiguientesEventos is null || day.Estado is not ("ABIERTA" or "CERRADA" or "INCOMPLETA")
            || (day.Estado == "ABIERTA") != (day.SiguientesEventos.Length > 0)
            || day.SiguientesEventos.Length > 2
            || day.SiguientesEventos.Length == 2
                && !day.SiguientesEventos.ToHashSet().SetEquals(["SALIDA", "INICIO_DESCANSO"]))
            throw Protocol("INVALID_DAY");
        foreach (var movement in day.Movimientos)
        {
            ParseEvent(movement.Evento);
            TerminalClock.Parse(movement.HoraEventoUtc);
        }
        foreach (var successor in day.SiguientesEventos)
            ParseEvent(successor);
    }

    private static void ValidateConfirmation(Confirmation confirmation, Guid id)
    {
        if (!confirmation.Accepted || confirmation.Status != "accepted" || confirmation.EventoId != id
            || confirmation.FuenteTemporal is not ("SERVIDOR" or "DISPOSITIVO_OFFLINE")
            || TerminalClock.Day(TerminalClock.Parse(confirmation.HoraEventoUtc)) != confirmation.JornadaFecha)
            throw Protocol("INVALID_CONFIRMATION");
    }

    private static void ValidateAudit(AuditPayload audit)
    {
        if (audit is null || audit.EventoId == Guid.Empty || audit.Tipo is not ("LECTURA_HUELLA" or "ALTA_HUELLA")
            || audit.Resultado is not ("EXITOSO" or "RECHAZADO" or "ERROR")
            || (audit.Tipo != "LECTURA_HUELLA" || audit.Resultado == "EXITOSO") && audit.EmpleadoId is not > 0
            || audit.Resultado == "EXITOSO" && audit.Metadata is not null)
            throw new IOException("Auditoría local inválida.");
        TerminalClock.Parse(audit.OcurridoEnUtc);
    }

    public void Dispose()
    {
        Gallery.Dispose();
        files.Dispose();
        refresh.Dispose();
        synchronization.Dispose();
        captures.Dispose();
    }

    private sealed class Session(OperationalTerminal terminal, long epoch) : IEnrollmentSession
    {
        private volatile bool disposed;
        public OperationalTerminal Owner => terminal;
        public bool IsActive
        {
            get { lock (terminal.gate) return !disposed && epoch == terminal.privilegeEpoch && terminal.CanAdministerLocked(); }
        }
        public Task<bool> CanEnrollAsync(string employeeId, CancellationToken cancellation) =>
            terminal.Authorize(this, employeeId, cancellation);
        public async Task<bool> CommitEnrollmentAsync(string employeeId, Action save, CancellationToken cancellation)
        {
            if (!await CanEnrollAsync(employeeId, cancellation).ConfigureAwait(false))
                return false;
            lock (terminal.gate)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!IsActive || terminal.FindEmployee(employeeId) is null)
                    return false;
                save();
                return true;
            }
        }
        public void Dispose() => disposed = true;
    }
}
