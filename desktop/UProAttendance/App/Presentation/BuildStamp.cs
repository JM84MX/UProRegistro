using System.Globalization;
using System.Reflection;

namespace UProAttendance.Presentation;

internal static class BuildStamp
{
    public static string? Read(TimeZoneInfo timeZone)
    {
        var assembly = typeof(BuildStamp).Assembly;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
        var utc = metadata.FirstOrDefault(attribute => attribute.Key == "DevelopmentBuildUtc")?.Value;
        var summary = metadata.FirstOrDefault(attribute => attribute.Key == "DevelopmentChangeSummary")?.Value;
        var version = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!DateTime.TryParseExact(utc, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var compiledAt)
            || compiledAt.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(version))
            return null;

        var localTime = TimeZoneInfo.ConvertTimeFromUtc(compiledAt, timeZone);
        var date = localTime.ToString("dd/MM/yyyy hh:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant();
        return $"v{version} ({date}) - {summary}";
    }
}
