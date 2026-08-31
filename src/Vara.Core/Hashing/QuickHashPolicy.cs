namespace Vara.Core.Hashing;

/// <summary>
/// Current scheme parameters for the bounded-prefix "quick hash" used to pre-filter
/// move-detection candidates before committing to a full-content read (backup-execution
/// spec's "Move detection avoids unbounded reads for non-matching candidates"
/// requirement). A recorded quick hash is only trusted for pre-filtering when its
/// scheme matches <see cref="CurrentScheme"/> exactly; bumping this constant when the
/// window size or algorithm changes makes older recorded quick hashes simply age out
/// of the fast path (treated as unavailable, falling back to a full read) rather than
/// requiring a migration or backfill - see this change's design.md.
/// </summary>
public static class QuickHashPolicy
{
    /// <summary>Identifies the window size/algorithm combination below. Bump when either changes.</summary>
    public const int CurrentScheme = 1;

    /// <summary>Number of leading bytes hashed to produce the quick hash.</summary>
    public const int WindowSizeBytes = 64 * 1024;
}
