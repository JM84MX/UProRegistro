using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using UProAttendance.Core.Editorial;

namespace UProAttendance;

internal static class LocalEditorialHistory
{
    public static EditorialMessages Create()
    {
        var laboratory = new Lazy<string>(() => ProtectDirectory(HistoryDirectory(EditorialEnvironment.Laboratory)));
        var operational = new Lazy<string>(() => ProtectDirectory(HistoryDirectory(EditorialEnvironment.Operational)));
        var reported = new ConcurrentDictionary<(EditorialEnvironment, EditorialIssue), byte>();

        string DirectoryFor(EditorialEnvironment environment) =>
            (environment == EditorialEnvironment.Laboratory ? laboratory : operational).Value;

        return new(MessageCatalog.LoadBundled, environment => new(DirectoryFor(environment)), (environment, issue) =>
        {
            if (!reported.TryAdd((environment, issue), 0))
                return;

            var diagnostic = $"{DateTimeOffset.UtcNow:O} {environment} {issue}";
            Trace.TraceWarning(diagnostic);
            File.WriteAllText(Path.Combine(DirectoryFor(environment), "editorial-error.log"), diagnostic + Environment.NewLine);
        });
    }

    private static string HistoryDirectory(EditorialEnvironment environment)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(root))
            throw new IOException("Windows did not provide a local application data directory.");

        return Path.Combine(root, "UProRegistro", "Editorial",
            environment == EditorialEnvironment.Laboratory ? "Laboratorio" : "Operacion");
    }

    internal static string ProtectDirectory(string folder)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("Windows identity has no user SID.");

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new(user, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));

        var directory = new DirectoryInfo(folder);
        directory.Create(security);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The editorial directory is a reparse point.");
        directory.SetAccessControl(security);
        return directory.FullName;
    }
}
