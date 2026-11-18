using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using UProAttendance.Core;

namespace UProAttendance;

internal enum TerminalError { Offline, Server, InvalidCredential, Revoked, Contract, Conflict, Missing, Denied }
internal sealed class TerminalException(TerminalError kind, string code) : Exception(code)
{
    public TerminalError Kind { get; } = kind;
    public bool Transient => Kind is TerminalError.Offline or TerminalError.Server;
}
internal sealed record DeviceIdentity(Uri Url, Guid DeviceId, string Nombre, string? DeviceToken, int VersionCredencial,
    TerminalError? Blocked = null);
internal sealed record Registration(string Token, Guid DeviceId, string Nombre);
internal sealed record Credential(Guid DeviceId, int VersionCredencial, string DeviceToken);
internal sealed record RemoteConfiguration(bool RegistroHuellaAdmon);
internal sealed record TemporalAuthorization(string Token, string ExpiraEnUtc);
internal sealed record Heartbeat(bool Authenticated, string ServerTimeUtc, string BusinessTimeZone,
    RemoteConfiguration Configuration, TemporalAuthorization AutorizacionTemporal);
internal sealed record RemoteEmployee(int Id, string? Alias, string NombreCompleto, string Puesto);
internal sealed record DirectoryResponse(RemoteEmployee[] Empleados);
internal sealed record EnrollmentRequest(int EmpleadoId);
internal sealed record EnrollmentResponse(bool Authorized, int EmpleadoId);
internal sealed record PunchPayload(Guid EventoId, int EmpleadoId, string Evento, string HoraDispositivoUtc,
    string ModoCaptura, string? AutorizacionTemporal);
internal sealed record Confirmation(bool Accepted, string Status, Guid EventoId, string HoraEventoUtc,
    DateOnly JornadaFecha, string FuenteTemporal);
internal sealed record RemoteMovement(string Evento, string HoraEventoUtc, string FuenteTemporal);
internal sealed record RemoteDay(int EmpleadoId, DateOnly JornadaFecha, string Estado, string[] SiguientesEventos,
    RemoteMovement[] Movimientos);
internal sealed record AuditMetadata(string Motivo);
internal sealed record AuditPayload(Guid EventoId, string Tipo, string Resultado, string OcurridoEnUtc,
    int? EmpleadoId = null, Guid? CorrelacionId = null, AuditMetadata? Metadata = null);
internal sealed record AuditReceipt(bool Accepted, Guid EventoId);
internal sealed record RemoteError(string Code);

internal static class TerminalClock
{
    private static readonly TimeZoneInfo MexicoCity = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");
    private static readonly string[] Formats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFF'Z'"];
    public static DateOnly Day(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, MexicoCity).DateTime);
    public static string Utc(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    public static DateTimeOffset Parse(string utc) =>
        DateTimeOffset.TryParseExact(utc, Formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant : throw new TerminalException(TerminalError.Contract, "INVALID_UTC");
}

internal sealed class UproCheckApi(HttpClient http)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public static Uri ValidateUrl(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || url.AbsolutePath != "/" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new ArgumentException("Introduce el origen HTTPS de UProApp.");
        return url;
    }

    public Task<Credential> Register(DeviceIdentity device, string token, CancellationToken ct) =>
        Send<Credential>(device, HttpMethod.Post, "registro-dispositivo", new Registration(token, device.DeviceId, device.Nombre), ct, false);
    public Task<Heartbeat> Heartbeat(DeviceIdentity device, CancellationToken ct) =>
        Send<Heartbeat>(device, HttpMethod.Post, "heartbeat", new { }, ct);
    public Task<DirectoryResponse> Employees(DeviceIdentity device, CancellationToken ct) =>
        Send<DirectoryResponse>(device, HttpMethod.Get, "empleados", null, ct);
    public Task<EnrollmentResponse> Authorize(DeviceIdentity device, int employee, CancellationToken ct) =>
        Send<EnrollmentResponse>(device, HttpMethod.Post, "enrolamiento/autorizacion", new EnrollmentRequest(employee), ct);
    public Task<Confirmation> Submit(DeviceIdentity device, PunchPayload punch, CancellationToken ct) =>
        Send<Confirmation>(device, HttpMethod.Post, "asistencia", punch, ct);
    public Task<Confirmation> Confirm(DeviceIdentity device, Guid id, CancellationToken ct) =>
        Send<Confirmation>(device, HttpMethod.Get, $"asistencia/{id:D}", null, ct);
    public Task<RemoteDay> Day(DeviceIdentity device, int employee, DateOnly day, CancellationToken ct) =>
        Send<RemoteDay>(device, HttpMethod.Get, FormattableString.Invariant($"jornada?empleadoId={employee}&jornadaFecha={day:yyyy-MM-dd}"), null, ct);
    public Task<AuditReceipt> Audit(DeviceIdentity device, AuditPayload audit, CancellationToken ct) =>
        Send<AuditReceipt>(device, HttpMethod.Post, "auditoria", audit, ct);

    private async Task<T> Send<T>(DeviceIdentity device, HttpMethod method, string route, object? payload,
        CancellationToken ct, bool authenticated = true)
    {
        using var request = new HttpRequestMessage(method, new Uri(ValidateUrl(device.Url.AbsoluteUri), "/api/uprocheck/" + route));
        if (authenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", device.DeviceToken);
            request.Headers.Add("X-UPro-Device-Id", device.DeviceId.ToString("D"));
        }
        if (payload is not null)
            request.Content = JsonContent.Create(payload, payload.GetType(), options: Json);

        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != (authenticated ? HttpStatusCode.OK : HttpStatusCode.Created))
                throw await Error(response, ct).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
                ?? throw new TerminalException(TerminalError.Contract, "EMPTY_RESPONSE");
        }
        catch (JsonException)
        {
            throw new TerminalException(TerminalError.Contract, "INVALID_JSON");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TerminalException(TerminalError.Server, "TIMEOUT");
        }
        catch (HttpRequestException error)
        {
            var kind = error.HttpRequestError == HttpRequestError.SecureConnectionError ? TerminalError.Contract
                : !NetworkInterface.GetIsNetworkAvailable() || error.HttpRequestError == HttpRequestError.NameResolutionError
                    ? TerminalError.Offline : TerminalError.Server;
            throw new TerminalException(kind, "TRANSPORT");
        }
    }

    private static async Task<TerminalException> Error(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null;
        try { code = (await response.Content.ReadFromJsonAsync<RemoteError>(Json, ct).ConfigureAwait(false))?.Code; }
        catch (JsonException) { }

        var kind = (response.StatusCode, code) switch
        {
            (_, "DEVICE_REVOKED") => TerminalError.Revoked,
            (HttpStatusCode.Unauthorized, _) => TerminalError.InvalidCredential,
            (HttpStatusCode.Forbidden, "ATTENDANCE_FOREIGN") => TerminalError.Conflict,
            (HttpStatusCode.Forbidden, _) => TerminalError.Denied,
            (HttpStatusCode.NotFound, _) => TerminalError.Missing,
            (HttpStatusCode.Conflict, _) => TerminalError.Conflict,
            (>= HttpStatusCode.InternalServerError, _) or (HttpStatusCode.TooManyRequests, _) => TerminalError.Server,
            _ => TerminalError.Contract
        };
        // Persist and display only recognized protocol codes.
        return new(kind, code switch
        {
            "ATTENDANCE_UUID_CONFLICT" or "ATTENDANCE_SEQUENCE_CONFLICT" or "ATTENDANCE_FOREIGN"
                or "DEVICE_ALREADY_REGISTERED" or "TEMPORAL_AUTHORIZATION_INVALID" or "DEVICE_REVOKED"
                or "REGISTRATION_TOKEN_INVALID" or "ATTENDANCE_NOT_FOUND" or "AUDIT_UUID_CONFLICT" => code,
            _ => $"HTTP_{(int)response.StatusCode}"
        });
    }

    public static int EmployeeId(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            && id.ToString(CultureInfo.InvariantCulture) == value ? id
            : throw new TerminalException(TerminalError.Contract, "INVALID_EMPLOYEE");

    public static Employee Map(RemoteEmployee employee)
    {
        if (employee.Id <= 0 || string.IsNullOrWhiteSpace(employee.NombreCompleto) || employee.Puesto is null)
            throw new TerminalException(TerminalError.Contract, "INVALID_EMPLOYEE");
        return new(employee.Id.ToString(CultureInfo.InvariantCulture), employee.NombreCompleto)
        {
            Alias = employee.Alias,
            Position = employee.Puesto.Trim().ToUpperInvariant() switch
            {
                "ESTILISTA" => EmployeePosition.Stylist,
                "RECEPCIONISTA" or "RECEPCIÓN" => EmployeePosition.Receptionist,
                "ASISTENTE" => EmployeePosition.Assistant,
                "APRENDIZ" => EmployeePosition.Apprentice,
                "GERENTE" => EmployeePosition.Manager,
                "CONTADOR" or "CONTADORA" => EmployeePosition.Accountant,
                "CEO" => EmployeePosition.Ceo,
                "DUEÑO" or "DUEÑA" or "PROPIETARIO" or "PROPIETARIA" => EmployeePosition.Owner,
                _ => EmployeePosition.Unknown
            }
        };
    }

    public static string EventName(AttendanceAction action) => action switch
    {
        AttendanceAction.Entry => "ENTRADA",
        AttendanceAction.BreakStart => "INICIO_DESCANSO",
        AttendanceAction.BreakEnd => "FIN_DESCANSO",
        AttendanceAction.Exit => "SALIDA",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}
