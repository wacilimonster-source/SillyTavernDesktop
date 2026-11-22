namespace SillyTavernTray;

/// <summary>
/// Pure decisions for the log archive naming and retention. The daily archive keeps
/// one file per calendar day (NSSM/WinSW style); a size overflow on the same day adds
/// a time suffix. Retention treats the legacy numeric backups (tray.log.N written by
/// older versions) as the oldest archives so old files age out first.
/// </summary>
internal static class LogRotationPolicy
{
    public const long MaximumBytes = 5L * 1024 * 1024;
    public const int MaximumArchives = 10;

    /// <summary>First-choice archive name for a rotation at the given local time.</summary>
    public static string PreferredArchiveName(DateTime stamp) => $"tray-{stamp:yyyyMMdd}.log";

    /// <summary>
    /// Resolves the archive name against the files already present: the preferred daily
    /// name when free, otherwise a same-day timestamped name (a size overflow earlier in
    /// the day must not overwrite the day's first archive).
    /// </summary>
    public static string ResolveArchiveName(DateTime stamp, IEnumerable<string> existingArchiveNames)
    {
        var preferred = PreferredArchiveName(stamp);
        var existing = existingArchiveNames as ICollection<string> ?? existingArchiveNames.ToList();
        if (!existing.Contains(preferred, StringComparer.OrdinalIgnoreCase)) return preferred;
        for (var second = 0; ; second++)
        {
            var candidate = $"tray-{stamp:yyyyMMdd-HHmmss}" + (second > 0 ? "-" + second.ToString("00") : "") + ".log";
            if (!existing.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;
        }
    }

    /// <summary>
    /// Archives to delete when over the retention cap, oldest first. Legacy numeric
    /// backups predate the dated names and always sort as oldest.
    /// </summary>
    public static IReadOnlyList<string> ExcessArchives(IEnumerable<string> archiveNames, int maximumArchives = MaximumArchives)
    {
        var ordered = archiveNames
            .OrderBy(name => name.StartsWith("tray.log.", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ordered.Count <= maximumArchives) return [];
        return ordered.Take(ordered.Count - maximumArchives).ToList();
    }
}
