namespace UProAttendance.Core;

public enum IdentificationStatus { NoMatch, Ambiguous, Accepted }
public sealed record IdentificationResult(IdentificationStatus Status, Identification? Identity = null);
public enum EnrollmentStatus { Accepted, Completed, Inconsistent, Duplicate, LimitReached, Denied }

public sealed class Biometrics(ITemplateGallery gallery)
{
    private readonly ITemplateGallery gallery = gallery;
    public const int SampleCount = 3;
    public bool HasEnrollments => gallery.Enrollments.Count > 0;

    public IdentificationResult Identify(
        FingerprintTemplate probe, IFingerprintMatcher matcher, CancellationToken cancellation)
    {
        Identification? candidate = null;
        var highMatch = false;
        foreach (var enrollment in gallery.Enrollments)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!MatchesAny(enrollment.Samples, probe, matcher, MatchLevel.Normal, cancellation))
                continue;

            if (candidate is not null)
                return new(IdentificationStatus.Ambiguous);

            candidate = enrollment.Identity;
            highMatch = MatchesAny(enrollment.Samples, probe, matcher, MatchLevel.High, cancellation);
        }

        return highMatch ? new(IdentificationStatus.Accepted, candidate) : new(IdentificationStatus.NoMatch);
    }

    public EnrollmentDraft Begin(Employee employee, Finger finger, IEnrollmentSession authorization) =>
        new(this, new(employee, finger), authorization);

    private static bool MatchesAny(IEnumerable<FingerprintTemplate> samples, FingerprintTemplate probe,
        IFingerprintMatcher matcher, MatchLevel level, CancellationToken cancellation)
    {
        foreach (var sample in samples)
        {
            cancellation.ThrowIfCancellationRequested();
            if (matcher.Matches(sample, probe, level))
                return true;
        }

        return false;
    }

    public sealed class EnrollmentDraft : IDisposable
    {
        private readonly Biometrics owner;
        private readonly IEnrollmentSession authorization;
        private readonly List<FingerprintTemplate> accepted = [];
        private bool closed;

        internal EnrollmentDraft(Biometrics owner, Identification identity, IEnrollmentSession authorization)
        {
            this.owner = owner;
            this.authorization = authorization;
            Identity = identity;
        }

        public Identification Identity { get; }
        public int AcceptedCount { get; private set; }

        // Takes ownership of the template even if the sample is rejected.
        public async Task<EnrollmentStatus> AcceptAsync(FingerprintTemplate sample, IFingerprintMatcher matcher,
            CancellationToken cancellation)
        {
            var retained = false;
            try
            {
                if (closed)
                    throw new ObjectDisposedException(nameof(EnrollmentDraft));
                if (!authorization.IsActive
                    || !await authorization.CanEnrollAsync(Identity.Employee.Id, cancellation).ConfigureAwait(false))
                    return EnrollmentStatus.Denied;

                cancellation.ThrowIfCancellationRequested();
                var enrollments = owner.gallery.Enrollments;
                if (enrollments.Any(item => item.Identity.Employee.Id == Identity.Employee.Id
                    && item.Identity.Finger == Identity.Finger))
                    return EnrollmentStatus.Duplicate;

                if (enrollments.Count(item => item.Identity.Employee.Id == Identity.Employee.Id) >= 3)
                    return EnrollmentStatus.LimitReached;

                foreach (var previous in accepted)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!matcher.Matches(previous, sample, MatchLevel.High))
                        return EnrollmentStatus.Inconsistent;
                }

                foreach (var enrollment in enrollments)
                {
                    if (MatchesAny(enrollment.Samples, sample, matcher, MatchLevel.Normal, cancellation))
                        return EnrollmentStatus.Duplicate;
                }

                cancellation.ThrowIfCancellationRequested();
                if (!authorization.IsActive)
                    return EnrollmentStatus.Denied;

                if (accepted.Count + 1 == SampleCount)
                {
                    if (!await authorization.CommitEnrollmentAsync(Identity.Employee.Id,
                        () => owner.gallery.Add(new(Identity, [.. accepted, sample])), cancellation).ConfigureAwait(false))
                        return EnrollmentStatus.Denied;
                    accepted.Clear();
                    closed = true;
                }
                else
                {
                    accepted.Add(sample);
                }

                retained = true;
                AcceptedCount++;
                return closed ? EnrollmentStatus.Completed : EnrollmentStatus.Accepted;
            }
            finally
            {
                if (!retained)
                    sample.Dispose();
            }
        }

        public void Dispose()
        {
            closed = true;
            foreach (var sample in accepted)
                sample.Dispose();
            accepted.Clear();
        }
    }
}

public sealed class SessionTemplateGallery : ITemplateGallery, IDisposable
{
    private readonly List<FingerEnrollment> enrollments = [];
    public IReadOnlyList<FingerEnrollment> Enrollments => enrollments;
    public void Add(FingerEnrollment enrollment) => enrollments.Add(enrollment);

    public void Dispose()
    {
        foreach (var enrollment in enrollments)
            foreach (var sample in enrollment.Samples)
                sample.Dispose();
        enrollments.Clear();
    }
}
