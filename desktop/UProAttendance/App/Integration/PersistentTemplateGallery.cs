using System.IO;
using System.Security.Cryptography;
using UProAttendance.Core;

namespace UProAttendance;

internal sealed class PersistentTemplateGallery : ITemplateGallery, IDisposable
{
    private readonly TerminalFiles files;
    private readonly SessionTemplateGallery gallery = new();
    private readonly List<AuditPayload> audits = [];
    private readonly object gate = new();

    public PersistentTemplateGallery(TerminalFiles files)
    {
        this.files = files;
        if (files.Read<GalleryState>("gallery") is not { } state)
            return;
        try
        {
            if (state.Fingers is null || state.Audits is null || state.Audits.Any(audit => audit.EventoId == Guid.Empty))
                throw new IOException("Galería local inválida.");
            foreach (var finger in state.Fingers)
            {
                if (finger.Employee is not { Id: not null } || !Enum.IsDefined(finger.Finger)
                    || finger.Samples is not { Length: Biometrics.SampleCount }
                    || finger.Samples.Any(sample => sample is not { Length: > 0 })
                    || gallery.Enrollments.Count(x => x.Identity.Employee.Id == finger.Employee.Id) >= 3
                    || gallery.Enrollments.Any(x => x.Identity.Employee.Id == finger.Employee.Id && x.Identity.Finger == finger.Finger))
                    throw new IOException("Plantillas locales inválidas.");
                UproCheckApi.EmployeeId(finger.Employee.Id);
                gallery.Add(new(new(finger.Employee, finger.Finger),
                    finger.Samples.Select(sample => new FingerprintTemplate(sample)).ToArray()));
            }
            audits.AddRange(state.Audits);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IReadOnlyList<FingerEnrollment> Enrollments => gallery.Enrollments;

    public AuditPayload[] Audits
    {
        get { lock (gate) return [.. audits]; }
    }

    public void Add(FingerEnrollment enrollment)
    {
        lock (gate)
        {
            var employee = UproCheckApi.EmployeeId(enrollment.Identity.Employee.Id);
            var audit = new AuditPayload(Guid.NewGuid(), "ALTA_HUELLA", "EXITOSO", TerminalClock.Utc(DateTimeOffset.UtcNow), employee);
            var fingers = gallery.Enrollments.Append(enrollment)
                .Select(item => new StoredFinger(item.Identity.Employee, item.Identity.Finger,
                    item.Samples.Select(sample => sample.Bytes).ToArray())).ToArray();
            files.Write("gallery", new GalleryState(fingers, [.. audits, audit]));
            gallery.Add(enrollment);
            audits.Add(audit);
        }
    }

    public void Dispose() => gallery.Dispose();

    private sealed record StoredFinger(Employee Employee, Finger Finger, byte[][] Samples);
    private sealed record GalleryState(StoredFinger[] Fingers, AuditPayload[] Audits);
}
