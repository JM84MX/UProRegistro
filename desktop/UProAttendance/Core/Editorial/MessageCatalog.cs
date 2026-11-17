using System.Text.Json;
using System.Text.Json.Serialization;

namespace UProAttendance.Core.Editorial;

public sealed record CatalogMessage(string Id, IReadOnlyList<string> Profiles, string Event, string Text);

public sealed class MessageCatalog
{
    private const string Resource = "UProAttendance.Catalog.Upro_catalogo_mensajes.json";
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly Dictionary<string, CatalogMessage> messages;

    private MessageCatalog(Dictionary<string, CatalogMessage> messages) => this.messages = messages;

    public IReadOnlyCollection<CatalogMessage> Messages => messages.Values;

    public static Stream OpenBundledResource() =>
        typeof(MessageCatalog).Assembly.GetManifestResourceStream(Resource)
        ?? throw new InvalidDataException("Missing bundled editorial catalog.");

    public static MessageCatalog LoadBundled()
    {
        using var stream = OpenBundledResource();
        return Load(stream);
    }

    public static MessageCatalog Load(Stream stream)
    {
        var source = JsonSerializer.Deserialize<CatalogDocument>(stream, JsonOptions);
        if (source is not { SchemaVersion: 1, Locale: "es-MX", Messages: { } rows })
            throw new InvalidDataException("Unsupported or incomplete editorial catalog.");

        var messages = new Dictionary<string, CatalogMessage>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.Text)
                || row.Event is not ("entry" or "exit") || row.Profiles is not { Length: > 0 } profiles
                || profiles.Any(profile => profile is not ("all" or "stylist" or "receptionist"))
                || profiles.Distinct(StringComparer.Ordinal).Count() != profiles.Length
                || !messages.TryAdd(row.Id, new(row.Id, Array.AsReadOnly(profiles), row.Event, row.Text)))
                throw new InvalidDataException("Invalid or duplicate editorial message.");
        }

        return new(messages);
    }

    public IReadOnlyList<CatalogMessage> Eligible(EmployeePosition position, string eventName)
    {
        var specific = position switch
        {
            EmployeePosition.Stylist => "stylist",
            EmployeePosition.Receptionist => "receptionist",
            _ => "all"
        };
        return messages.Values.Where(message => message.Event == eventName
            && (message.Profiles.Contains("all") || message.Profiles.Contains(specific))).ToArray();
    }

    internal CatalogMessage? Find(string id) => messages.GetValueOrDefault(id);

    private sealed record CatalogDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] string Locale,
        [property: JsonRequired] CatalogRow?[] Messages);

    private sealed record CatalogRow(
        [property: JsonRequired] string Id,
        [property: JsonRequired] string[] Profiles,
        [property: JsonRequired] string Event,
        [property: JsonRequired] string Text);
}
