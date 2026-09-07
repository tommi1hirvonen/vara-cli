using Vara.Core.Abstractions;
using Vara.Core.FileSystem;
using Vara.Core.Snapshots;

namespace Vara.Application.History;

/// <summary>
/// One file to be written as part of a directory restore's planned reconstruction of a point in
/// time, per <see cref="SnapshotHistoryService.PlanDirectoryRestore"/>.
/// </summary>
public sealed record DirectoryRestoreEntry(string RelativePath, string ContentHash, long Size, string DestinationPath);

/// <summary>
/// The full set of writes and removals a directory restore will perform, computed entirely from
/// manifest data before any file is touched, per the snapshot-history capability's "Restore a
/// directory at a given date" requirement - so a single confirmation can report exact counts
/// before anything changes. <see cref="Skipped"/> lists every path tracked as live at the
/// requested date as a symlink/junction entry - it has no stored content to extract, so it is
/// omitted from <see cref="ToWrite"/> (and never added to <see cref="ToRemove"/> either) rather
/// than causing the whole restore to fail.
/// </summary>
public sealed record DirectoryRestorePlan(
    IReadOnlyList<DirectoryRestoreEntry> ToWrite,
    IReadOnlyList<string> ToRemove,
    long TotalBytes,
    IReadOnlyList<string> Skipped);

/// <summary>
/// Browsing recorded snapshots/versions and restoring historical file content for a
/// single profile, per the snapshot-history spec. One instance is scoped to one
/// profile's manifest and content store.
/// </summary>
public sealed class SnapshotHistoryService(ISnapshotRepository repository, IContentStore contentStore)
{
    /// <summary>All recorded snapshots for the profile, most recent first.</summary>
    public IReadOnlyList<Snapshot> ListSnapshots() => repository.ListSnapshots();

    /// <summary>
    /// The full recorded history of a path, most recent first.
    /// </summary>
    /// <exception cref="NoHistoryForPathException">The path was never part of any recorded snapshot.</exception>
    public IReadOnlyList<FileVersionRecord> GetFileHistory(string relativePath)
    {
        var history = repository.GetFileHistory(relativePath);
        if (history.Count == 0)
        {
            throw new NoHistoryForPathException(relativePath);
        }

        return history;
    }

    /// <summary>
    /// Extracts a path's content as it existed as of <paramref name="asOf"/> to
    /// <paramref name="destinationPath"/>, without touching the live mirror. Works for
    /// files later deleted from the source, as long as <paramref name="asOf"/> predates
    /// the deletion.
    /// </summary>
    /// <param name="overwrite">
    /// When <c>false</c> (the default), an existing file at <paramref name="destinationPath"/>
    /// causes <see cref="DestinationExistsException"/> rather than being overwritten. Has no
    /// effect on the mirror-containment guard, which is never overridable.
    /// </param>
    /// <param name="onBytesCopied">
    /// When given, invoked with each chunk's size as the matched version's content is copied,
    /// forwarded straight to <see cref="IContentStore.ExtractTo"/>, so a caller can report
    /// progress incrementally during a large restore rather than only once it completes.
    /// </param>
    /// <param name="onSizeResolved">
    /// When given, invoked once with the matched version's total byte count after the version
    /// has been resolved but before its content is copied, so a caller can size a progress
    /// indicator (for example a progress bar's maximum value) before extraction begins.
    /// </param>
    /// <exception cref="NoHistoryForPathException">The path was never part of any recorded snapshot.</exception>
    /// <exception cref="NoMatchingVersionException">No version of the path existed as of that date.</exception>
    /// <exception cref="RestoreLinkedEntryException">The resolved version is a symlink/junction entry with no stored content.</exception>
    /// <exception cref="RestoreDestinationInMirrorException">The destination resolves inside the profile's live mirror.</exception>
    /// <exception cref="DestinationExistsException">The destination already exists and <paramref name="overwrite"/> is <c>false</c>.</exception>
    public void RestoreAsOf(
        string relativePath,
        DateTimeOffset asOf,
        string destinationPath,
        bool overwrite = false,
        Action<long>? onBytesCopied = null,
        Action<long>? onSizeResolved = null)
    {
        var match = ResolveVersion(relativePath, versionId: null, asOf);
        GuardNotLinked(relativePath, match);

        GuardDestination(destinationPath, overwrite);
        onSizeResolved?.Invoke(match.Size);
        contentStore.ExtractTo(match.ContentHash!, destinationPath, onBytesCopied);
    }

    /// <summary>
    /// Extracts a path's content as recorded by the specific version <paramref name="versionId"/>
    /// (as shown by <see cref="GetFileHistory"/>) to <paramref name="destinationPath"/>.
    /// </summary>
    /// <param name="overwrite">
    /// When <c>false</c> (the default), an existing file at <paramref name="destinationPath"/>
    /// causes <see cref="DestinationExistsException"/> rather than being overwritten. Has no
    /// effect on the mirror-containment guard, which is never overridable.
    /// </param>
    /// <param name="onBytesCopied">
    /// When given, invoked with each chunk's size as the matched version's content is copied,
    /// forwarded straight to <see cref="IContentStore.ExtractTo"/>, so a caller can report
    /// progress incrementally during a large restore rather than only once it completes.
    /// </param>
    /// <param name="onSizeResolved">
    /// When given, invoked once with the matched version's total byte count after the version
    /// has been resolved but before its content is copied, so a caller can size a progress
    /// indicator (for example a progress bar's maximum value) before extraction begins.
    /// </param>
    /// <exception cref="NoHistoryForPathException">The path was never part of any recorded snapshot.</exception>
    /// <exception cref="NoMatchingVersionException">No such version id exists for the path.</exception>
    /// <exception cref="RestoreLinkedEntryException">The resolved version is a symlink/junction entry with no stored content.</exception>
    /// <exception cref="RestoreDestinationInMirrorException">The destination resolves inside the profile's live mirror.</exception>
    /// <exception cref="DestinationExistsException">The destination already exists and <paramref name="overwrite"/> is <c>false</c>.</exception>
    public void RestoreVersion(
        string relativePath,
        long versionId,
        string destinationPath,
        bool overwrite = false,
        Action<long>? onBytesCopied = null,
        Action<long>? onSizeResolved = null)
    {
        var match = ResolveVersion(relativePath, versionId, asOf: null);
        GuardNotLinked(relativePath, match);

        GuardDestination(destinationPath, overwrite);
        onSizeResolved?.Invoke(match.Size);
        contentStore.ExtractTo(match.ContentHash!, destinationPath, onBytesCopied);
    }

    /// <summary>
    /// Streams a path's content - resolved by <paramref name="versionId"/> or
    /// <paramref name="asOf"/>, exactly as <see cref="RestoreVersion"/>/<see cref="RestoreAsOf"/>
    /// resolve theirs - directly to <paramref name="destination"/>, without writing anything to
    /// disk. Exactly one of <paramref name="versionId"/>/<paramref name="asOf"/> must be given.
    /// Unless <paramref name="forceBinary"/> is <see langword="true"/>, the resolved content is
    /// checked with the same <see cref="LooksBinary"/> heuristic <see cref="OpenVersionsForDiff"/>
    /// uses, before any of it is copied to <paramref name="destination"/>.
    /// </summary>
    /// <param name="forceBinary">
    /// When <see langword="true"/>, skips the binary-content check entirely - the caller has
    /// already decided streaming is safe (destination is redirected) or explicitly opted in.
    /// </param>
    /// <exception cref="NoHistoryForPathException">The path was never part of any recorded snapshot.</exception>
    /// <exception cref="NoMatchingVersionException">No such version id/date exists for the path.</exception>
    /// <exception cref="ShowOrDiffLinkedEntryException">The resolved version is a symlink/junction entry with no stored content.</exception>
    /// <exception cref="ShowBinaryContentException">The resolved content is detected as binary and <paramref name="forceBinary"/> is <see langword="false"/>.</exception>
    public void ShowVersion(string relativePath, long? versionId, DateTimeOffset? asOf, Stream destination, bool forceBinary = false)
    {
        var match = ResolveVersion(relativePath, versionId, asOf);
        GuardNotLinkedForShowOrDiff(relativePath, match);

        using var source = contentStore.OpenRead(match.ContentHash!);
        if (!forceBinary && LooksBinary(source))
        {
            throw new ShowBinaryContentException(relativePath);
        }

        source.CopyTo(destination);
    }

    /// <summary>
    /// The largest a single resolved version's recorded size may be for <see cref="OpenVersionsForDiff"/>
    /// to diff it, per the snapshot-history spec's "Refusing an oversized version" scenario.
    /// </summary>
    private const long MaxDiffContentSize = 10 * 1024 * 1024;

    /// <summary>
    /// The number of leading bytes sampled to detect binary content in
    /// <see cref="OpenVersionsForDiff"/> and <see cref="ShowVersion"/>, per the
    /// snapshot-history spec's "Refusing binary content" scenarios. Matches the sample size
    /// Git itself uses for the same NUL-byte heuristic.
    /// </summary>
    private const int BinarySampleSize = 8000;

    /// <summary>
    /// Opens two versions of the same path - each resolved by its own version id or "as of"
    /// date, independently, exactly as <see cref="RestoreVersion"/>/<see cref="RestoreAsOf"/>
    /// resolve theirs - for a caller to diff. The caller owns and disposes both streams. Refuses
    /// the comparison, before either side's full content is read, when either side's recorded
    /// size exceeds <see cref="MaxDiffContentSize"/> or either side's content is detected as
    /// binary from its leading <see cref="BinarySampleSize"/> bytes.
    /// </summary>
    /// <exception cref="NoHistoryForPathException">The path was never part of any recorded snapshot.</exception>
    /// <exception cref="NoMatchingVersionException">No such version id/date exists for either side.</exception>
    /// <exception cref="DiffContentTooLargeException">Either resolved version's recorded size exceeds the diff size limit.</exception>
    /// <exception cref="DiffBinaryContentException">Either resolved version's content is detected as binary.</exception>
    /// <exception cref="ShowOrDiffLinkedEntryException">Either resolved version is a symlink/junction entry with no stored content.</exception>
    public (Stream Left, Stream Right) OpenVersionsForDiff(
        string relativePath,
        long? leftVersionId,
        DateTimeOffset? leftAsOf,
        long? rightVersionId,
        DateTimeOffset? rightAsOf)
    {
        var left = ResolveVersion(relativePath, leftVersionId, leftAsOf);
        var right = ResolveVersion(relativePath, rightVersionId, rightAsOf);

        var leftTooLarge = left.Size > MaxDiffContentSize;
        var rightTooLarge = right.Size > MaxDiffContentSize;
        if (leftTooLarge || rightTooLarge)
        {
            throw new DiffContentTooLargeException(
                relativePath,
                MaxDiffContentSize,
                leftTooLarge ? left.Size : null,
                rightTooLarge ? right.Size : null);
        }

        var leftLinked = left.ChangeKind == FileChangeKind.Linked;
        var rightLinked = right.ChangeKind == FileChangeKind.Linked;
        if (leftLinked || rightLinked)
        {
            throw new ShowOrDiffLinkedEntryException(relativePath, leftLinked, rightLinked);
        }

        Stream? leftStream = null;
        Stream? rightStream = null;
        try
        {
            leftStream = contentStore.OpenRead(left.ContentHash!);
            rightStream = contentStore.OpenRead(right.ContentHash!);
            var leftIsBinary = LooksBinary(leftStream);
            var rightIsBinary = LooksBinary(rightStream);
            if (leftIsBinary || rightIsBinary)
            {
                throw new DiffBinaryContentException(relativePath, leftIsBinary, rightIsBinary);
            }

            return (leftStream, rightStream);
        }
        catch
        {
            leftStream?.Dispose();
            rightStream?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads up to <see cref="BinarySampleSize"/> leading bytes from <paramref name="stream"/>
    /// and checks them for a NUL byte - the same heuristic Git uses to detect binary content -
    /// then rewinds the stream back to its start so a caller that goes on to read it (or is
    /// itself refused for another reason) sees its content from the beginning. Shared by
    /// <see cref="OpenVersionsForDiff"/> and <see cref="ShowVersion"/>.
    /// </summary>
    private static bool LooksBinary(Stream stream)
    {
        var buffer = new byte[BinarySampleSize];
        var totalRead = 0;
        int bytesRead;
        while (totalRead < buffer.Length && (bytesRead = stream.Read(buffer, totalRead, buffer.Length - totalRead)) > 0)
        {
            totalRead += bytesRead;
        }

        stream.Position = 0;
        return Array.IndexOf(buffer, (byte)0, 0, totalRead) >= 0;
    }

    /// <summary>
    /// Lists the immediate (one-level) contents of <paramref name="directoryPath"/> within the
    /// profile's mirror, per the backup-browsing capability's directory-listing requirements.
    /// Reflects the current state when <paramref name="asOf"/> is <c>null</c>, or the state as
    /// of that date otherwise. When <paramref name="includeDeleted"/> is <c>true</c>, deleted
    /// entries (and wholly-deleted subdirectories) are interleaved among the live ones, each
    /// marked <see cref="DirectoryEntryStatus.Deleted"/> or, if the entry was moved elsewhere
    /// rather than deleted outright, <see cref="DirectoryEntryStatus.Moved"/>.
    /// </summary>
    /// <exception cref="NoSuchDirectoryException">
    /// No tracked path, at any point in history, falls under <paramref name="directoryPath"/> -
    /// regardless of <paramref name="includeDeleted"/>, since a directory that currently has only
    /// deleted content is still a directory that was tracked.
    /// </exception>
    public IReadOnlyList<DirectoryEntry> ListDirectory(string directoryPath, DateTimeOffset? asOf, bool includeDeleted)
    {
        var prefix = NormalizeDirectoryPrefix(directoryPath);
        var live = asOf is null ? repository.GetCurrentState() : repository.GetStateAsOf(asOf.Value);
        var tombstones = repository.GetTombstones(asOf);
        var moveOrigins = repository.GetMoveOrigins();

        var entries = new Dictionary<string, DirectoryEntry>(StringComparer.OrdinalIgnoreCase);
        var anyTracked = false;

        foreach (var (path, state) in live)
        {
            if (!TryGetImmediateChild(path, prefix, out var name, out var isLeaf))
            {
                continue;
            }

            anyTracked = true;
            if (isLeaf)
            {
                entries[name] = new DirectoryEntry(name, DirectoryEntryKind.File, DirectoryEntryStatus.Live, state.Size);
            }
            else if (!entries.TryGetValue(name, out var existingDir) || existingDir.Status != DirectoryEntryStatus.Live)
            {
                entries[name] = new DirectoryEntry(name, DirectoryEntryKind.Directory, DirectoryEntryStatus.Live, null);
            }
        }

        foreach (var record in tombstones)
        {
            if (!TryGetImmediateChild(record.RelativePath, prefix, out var name, out var isLeaf))
            {
                continue;
            }

            anyTracked = true;
            if (!includeDeleted)
            {
                continue;
            }

            if (isLeaf)
            {
                if (entries.ContainsKey(name))
                {
                    continue;
                }

                var moved = moveOrigins.TryGetValue(record.RelativePath, out var movedTo);
                entries[name] = new DirectoryEntry(
                    name,
                    DirectoryEntryKind.File,
                    moved ? DirectoryEntryStatus.Moved : DirectoryEntryStatus.Deleted,
                    record.Size,
                    moved ? movedTo : null);
            }
            else if (!entries.ContainsKey(name))
            {
                entries[name] = new DirectoryEntry(name, DirectoryEntryKind.Directory, DirectoryEntryStatus.Deleted, null);
            }
        }

        if (!anyTracked)
        {
            throw new NoSuchDirectoryException(directoryPath);
        }

        return entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Computes the full write/remove plan for restoring every tracked path under
    /// <paramref name="directoryPath"/> to its state as of <paramref name="asOf"/> (or the
    /// current state, if <c>null</c>), per the snapshot-history capability's "Restore a
    /// directory at a given date" requirement. Every path tracked as live at that date is
    /// planned for writing (<see cref="DirectoryRestorePlan.ToWrite"/>), including one later
    /// deleted from the profile; a path currently tracked as live under the directory but not
    /// live at that date is planned for removal (<see cref="DirectoryRestorePlan.ToRemove"/>)
    /// if it currently exists at its computed destination. Every computed destination is
    /// validated against the live mirror before this method returns, so a violation aborts the
    /// whole plan rather than causing a partial restore.
    /// </summary>
    /// <param name="outRoot">
    /// The destination directory each write is placed under, relative to
    /// <paramref name="directoryPath"/>'s own prefix. Required (and used) unless
    /// <paramref name="inPlace"/> is <c>true</c>.
    /// </param>
    /// <param name="inPlace">
    /// When <c>true</c>, each tracked path is restored to its own original absolute source
    /// location (via <see cref="AbsolutePathMirrorMapper.FromMirrorPath"/>) instead of under
    /// <paramref name="outRoot"/>.
    /// </param>
    /// <exception cref="NoSuchDirectoryException">
    /// No tracked path, at any point in history, falls under <paramref name="directoryPath"/>.
    /// </exception>
    /// <exception cref="RestoreDestinationInMirrorException">
    /// Any computed destination resolves inside the profile's live mirror.
    /// </exception>
    public DirectoryRestorePlan PlanDirectoryRestore(string directoryPath, DateTimeOffset? asOf, string? outRoot, bool inPlace)
    {
        var prefix = NormalizeDirectoryPrefix(directoryPath);
        var historical = asOf is null ? repository.GetCurrentState() : repository.GetStateAsOf(asOf.Value);
        var current = repository.GetCurrentState();
        var anyTracked = false;

        string ComputeDestination(string relativePath, string subPath) =>
            inPlace ? AbsolutePathMirrorMapper.FromMirrorPath(relativePath) : Path.Combine(outRoot!, subPath);

        var toWrite = new List<DirectoryRestoreEntry>();
        var skipped = new List<string>();
        var historicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, state) in historical)
        {
            if (!TryGetDescendantSubPath(path, prefix, out var subPath))
            {
                continue;
            }

            anyTracked = true;
            historicalPaths.Add(path);

            // A path tracked as live at the requested date as a symlink/junction has no
            // stored content to extract (its ContentHash is always null) - it is
            // reported as skipped rather than written, and (via historicalPaths above)
            // also never added to ToRemove even though it contributes nothing to
            // ToWrite, per the "Restore a directory at a given date" requirement.
            if (state.IsLinked)
            {
                skipped.Add(path);
                continue;
            }

            toWrite.Add(new DirectoryRestoreEntry(path, state.ContentHash!, state.Size, ComputeDestination(path, subPath)));
        }

        var toRemove = new List<string>();
        foreach (var (path, _) in current)
        {
            if (!TryGetDescendantSubPath(path, prefix, out var subPath))
            {
                continue;
            }

            anyTracked = true;
            if (historicalPaths.Contains(path))
            {
                continue;
            }

            var destination = ComputeDestination(path, subPath);
            if (contentStore.TargetExists(destination))
            {
                toRemove.Add(destination);
            }
        }

        if (!anyTracked)
        {
            foreach (var record in repository.GetTombstones(asOf))
            {
                if (TryGetDescendantSubPath(record.RelativePath, prefix, out _))
                {
                    anyTracked = true;
                    break;
                }
            }
        }

        if (!anyTracked)
        {
            throw new NoSuchDirectoryException(directoryPath);
        }

        foreach (var entry in toWrite)
        {
            if (contentStore.IsWithinMirror(entry.DestinationPath))
            {
                throw new RestoreDestinationInMirrorException(entry.DestinationPath);
            }
        }

        foreach (var destination in toRemove)
        {
            if (contentStore.IsWithinMirror(destination))
            {
                throw new RestoreDestinationInMirrorException(destination);
            }
        }

        return new DirectoryRestorePlan(toWrite, toRemove, toWrite.Sum(e => e.Size), skipped);
    }

    /// <summary>
    /// Executes a previously computed <see cref="DirectoryRestorePlan"/>: reports the plan's
    /// total byte count once via <paramref name="onSizeResolved"/>, then removes every
    /// <see cref="DirectoryRestorePlan.ToRemove"/> destination (fast, with no byte content to
    /// report), and finally writes every <see cref="DirectoryRestorePlan.ToWrite"/> entry,
    /// forwarding <paramref name="onBytesCopied"/> through each entry's
    /// <see cref="IContentStore.ExtractTo"/> call so a caller can render one
    /// continuously-advancing progress indicator across the whole operation.
    /// </summary>
    public void ExecuteDirectoryRestore(DirectoryRestorePlan plan, Action<long>? onBytesCopied = null, Action<long>? onSizeResolved = null)
    {
        onSizeResolved?.Invoke(plan.TotalBytes);

        foreach (var destination in plan.ToRemove)
        {
            contentStore.RemoveExtractedFile(destination);
        }

        foreach (var entry in plan.ToWrite)
        {
            contentStore.ExtractTo(entry.ContentHash, entry.DestinationPath, onBytesCopied);
        }
    }

    /// <summary>
    /// Reports deleted files, most recently deleted first, optionally scoped to a subtree
    /// (<paramref name="directoryPath"/>) and/or bounded to deletions recorded at or after
    /// <paramref name="since"/>, per the backup-browsing capability's recently-deleted report.
    /// </summary>
    public IReadOnlyList<FileVersionRecord> ListDeleted(string? directoryPath, DateTimeOffset? since)
    {
        IEnumerable<FileVersionRecord> tombstones = repository.GetTombstones(asOf: null);

        var prefix = NormalizeDirectoryPrefix(directoryPath ?? string.Empty);
        if (prefix.Length > 0)
        {
            tombstones = tombstones.Where(r => r.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        if (since is not null)
        {
            tombstones = tombstones.Where(r => r.RecordedAt >= since.Value);
        }

        return tombstones.OrderByDescending(r => r.RecordedAt).ToList();
    }

    /// <summary>
    /// Resolves the version a restore/show/diff operation should act on: by
    /// <paramref name="versionId"/> if given, otherwise by <paramref name="asOf"/>. Exactly one
    /// must be given (a defensive assumption both callers already guarantee at the CLI layer).
    /// </summary>
    /// <exception cref="NoHistoryForPathException">The path was never part of any recorded snapshot.</exception>
    /// <exception cref="NoMatchingVersionException">No such version id/date exists for the path.</exception>
    private FileVersionRecord ResolveVersion(string relativePath, long? versionId, DateTimeOffset? asOf)
    {
        var history = repository.GetFileHistory(relativePath);
        if (history.Count == 0)
        {
            throw new NoHistoryForPathException(relativePath);
        }

        if (versionId is not null)
        {
            return history.FirstOrDefault(r => r.Id == versionId.Value && r.ChangeKind != FileChangeKind.Deleted)
                ?? throw new NoMatchingVersionException(relativePath, versionId.Value);
        }

        return history.FirstOrDefault(r => r.RecordedAt <= asOf!.Value) is { ChangeKind: not FileChangeKind.Deleted } match
            ? match
            : throw new NoMatchingVersionException(relativePath, asOf!.Value);
    }

    /// <summary>
    /// Guards against extracting a resolved version that is a <see cref="FileChangeKind.Linked"/>
    /// entry - a symlink/junction has no content-store hash to extract
    /// (<see cref="FileVersionRecord.ContentHash"/> is always <c>null</c> for one), so calling
    /// <see cref="IContentStore.ExtractTo"/> for it would fail with an unrelated internal
    /// error rather than a clear, actionable message.
    /// </summary>
    /// <exception cref="RestoreLinkedEntryException">The resolved version is a symlink/junction entry.</exception>
    private static void GuardNotLinked(string relativePath, FileVersionRecord match)
    {
        if (match.ChangeKind == FileChangeKind.Linked)
        {
            throw new RestoreLinkedEntryException(relativePath);
        }
    }

    /// <summary>
    /// Single-sided sibling of <see cref="GuardNotLinked"/> for <see cref="ShowVersion"/>: same
    /// <see cref="FileChangeKind.Linked"/> check, but throws <see cref="ShowOrDiffLinkedEntryException"/>
    /// instead of <see cref="RestoreLinkedEntryException"/>, since <c>show</c> is not a restore.
    /// <see cref="OpenVersionsForDiff"/> has its own two-sided check instead of calling this, since
    /// it needs to name which of its two independently-resolved sides is linked in one error,
    /// mirroring how it already reports oversized/binary content on either side.
    /// </summary>
    /// <exception cref="ShowOrDiffLinkedEntryException">The resolved version is a symlink/junction entry.</exception>
    private static void GuardNotLinkedForShowOrDiff(string relativePath, FileVersionRecord match)
    {
        if (match.ChangeKind == FileChangeKind.Linked)
        {
            throw new ShowOrDiffLinkedEntryException(relativePath);
        }
    }

    /// <summary>
    /// Normalizes a user-supplied directory path to the trailing-separator-terminated prefix
    /// used to match tracked paths against it - <c>""</c> for the mirror root (empty, <c>.</c>,
    /// or whitespace-only input), otherwise the trimmed path with exactly one trailing
    /// backslash appended, so prefix matching only ever matches whole path segments.
    /// </summary>
    private static string NormalizeDirectoryPrefix(string directoryPath)
    {
        var trimmed = directoryPath.Trim().Trim('\\', '/');
        return trimmed.Length == 0 || trimmed == "." ? string.Empty : trimmed + "\\";
    }

    /// <summary>
    /// Whether <paramref name="relativePath"/> falls under <paramref name="prefix"/> (as
    /// produced by <see cref="NormalizeDirectoryPrefix"/>), and if so, its immediate child
    /// segment's name and whether that segment is the path's final one (a file directly in
    /// the listed directory) or an intermediate one (a subdirectory to synthesize).
    /// </summary>
    private static bool TryGetImmediateChild(string relativePath, string prefix, out string name, out bool isLeaf)
    {
        name = string.Empty;
        isLeaf = false;

        string remainder;
        if (prefix.Length == 0)
        {
            remainder = relativePath;
        }
        else
        {
            if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            remainder = relativePath[prefix.Length..];
        }

        if (remainder.Length == 0)
        {
            return false;
        }

        var separatorIndex = remainder.IndexOfAny(['\\', '/']);
        if (separatorIndex < 0)
        {
            name = remainder;
            isLeaf = true;
            return true;
        }

        name = remainder[..separatorIndex];
        isLeaf = false;
        return name.Length > 0;
    }

    /// <summary>
    /// Whether <paramref name="relativePath"/> falls under <paramref name="prefix"/> (as
    /// produced by <see cref="NormalizeDirectoryPrefix"/>), at any depth - unlike
    /// <see cref="TryGetImmediateChild"/>, which only matches the prefix's immediate children.
    /// Used by directory restore, which needs every descendant, not just one level. When it
    /// falls under the prefix, <paramref name="subPath"/> is the portion of
    /// <paramref name="relativePath"/> after the prefix - the path's location relative to the
    /// requested directory, used to compute a directory restore's destination.
    /// </summary>
    private static bool TryGetDescendantSubPath(string relativePath, string prefix, out string subPath)
    {
        if (prefix.Length == 0)
        {
            subPath = relativePath;
            return true;
        }

        if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            subPath = string.Empty;
            return false;
        }

        subPath = relativePath[prefix.Length..];
        return true;
    }

    /// <summary>
    /// Guards a restore destination: refuses unconditionally if it resolves inside the live
    /// mirror, and refuses if it already exists unless <paramref name="overwrite"/> is true.
    /// </summary>
    private void GuardDestination(string destinationPath, bool overwrite)
    {
        if (contentStore.IsWithinMirror(destinationPath))
        {
            throw new RestoreDestinationInMirrorException(destinationPath);
        }

        if (!overwrite && contentStore.TargetExists(destinationPath))
        {
            throw new DestinationExistsException(destinationPath);
        }
    }
}
