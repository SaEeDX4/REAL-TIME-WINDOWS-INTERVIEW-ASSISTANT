namespace InterviewAssistant.Core.Domain;

/// <summary>
/// Upgrades stored profiles to the current schema without losing anything: RC1 profiles (résumé facts only) gain
/// provenance (source, file name, timestamps). Confirmation state is never changed by a migration.
/// </summary>
public static class ProfileMigration
{
    /// <summary>Returns true when the profile was changed (the caller may persist it).</summary>
    public static bool Migrate(CandidateProfileRecord p)
    {
        if (p.SchemaVersion >= CandidateProfileRecord.CurrentSchemaVersion) return false;
        // Tolerates duplicate document ids (hand-edited or damaged files) instead of making the profile unloadable.
        var docs = p.Documents.Where(d => d.Id != null).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First().FileName);
        foreach (var f in p.Facts)
        {
            if (f.Source == FactSource.Unknown && f.Provenance != null)
            {
                f.Source = FactSource.ResumeImport;
                f.SourceName = docs.GetValueOrDefault(f.Provenance.DocumentId, f.SourceName);
            }
            f.CreatedUtc ??= p.CreatedUtc;
            if (f.Status == FactStatus.UserConfirmed) f.ConfirmedUtc ??= p.UpdatedUtc;
        }
        p.SchemaVersion = CandidateProfileRecord.CurrentSchemaVersion;
        return true;
    }
}
