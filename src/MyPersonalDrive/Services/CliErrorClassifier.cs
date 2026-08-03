namespace MyPersonalDrive.Services;

/// <summary>
/// Classifies a failed Proton Drive CLI invocation into a <see cref="CliErrorKind"/>.
/// The Proton Drive CLI does not expose distinct exit codes per failure type (as of the last
/// time this was checked - see docs/PLAN-LOCAL-SYNC.md Phase 0, question #10), so this is
/// necessarily substring matching against English-language CLI output. That is a known
/// limitation, not an oversight: keeping it isolated here means the day the CLI provides real
/// exit codes, only this file needs to change.
/// </summary>
internal static class CliErrorClassifier
{
    public static CliErrorKind Classify(int exitCode, string stdout, string stderr)
    {
        // Both streams, not one or the other: when the CLI crashes internally it writes a bare
        // `====` banner to stderr and the actual diagnosis to stdout, so preferring stderr threw
        // away the only useful text (verified — docs/PLAN-LOCAL-SYNC.md Appendix A #11).
        var text = string.Concat(stderr, "\n", stdout);

        if (Contains(text, "SQLITE_BUSY") || Contains(text, "database is locked"))
        {
            return CliErrorKind.Busy;
        }

        if (Contains(text, "login first") || Contains(text, "not authenticated") || Contains(text, "not logged in"))
        {
            return CliErrorKind.NotAuthenticated;
        }

        if (Contains(text, "does not exist") || Contains(text, "not found") || Contains(text, "no such"))
        {
            return CliErrorKind.NotFound;
        }

        if (Contains(text, "already exists"))
        {
            return CliErrorKind.AlreadyExists;
        }

        if (Contains(text, "quota") || Contains(text, "storage limit") || Contains(text, "not enough space"))
        {
            return CliErrorKind.Quota;
        }

        if (Contains(text, "permission denied") || Contains(text, "forbidden") || Contains(text, "access denied"))
        {
            return CliErrorKind.PermissionDenied;
        }

        if (Contains(text, "network") || Contains(text, "connection") || Contains(text, "timed out"))
        {
            return CliErrorKind.Network;
        }

        return CliErrorKind.Unknown;
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
