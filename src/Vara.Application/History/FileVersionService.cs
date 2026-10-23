using Vara.Core.Abstractions;
using Vara.Core.Snapshots;

namespace Vara.Application.History;

/// <summary>
/// Reading a single path's historical content out of the content store for a profile - restoring
/// it to disk, streaming it, or opening two versions for a diff - per the snapshot-history spec.
/// One instance is scoped to one profile's manifest and content store.
/// </summary>
public sealed class FileVersionService(ISnapshotRepository repository, IContentStore contentStore)
{
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
    /// <exception cref="DestinationExistsException">
    /// The destination already exists and <paramref name="overwrite"/> is <c>false</c>.
    /// </exception>
    public void RestoreAsOf(
        string relativePath,
        DateTimeOffset asOf,
        string destinationPath,
        bool overwrite = false,
        Action<long>? onBytesCopied = null,
        Action<long>? onSizeResolved = null) =>
        Restore(ResolveVersion(relativePath, versionId: null, asOf), relativePath, destinationPath, overwrite, onBytesCopied, onSizeResolved);

    /// <summary>
    /// Extracts a path's content as recorded by the specific version <paramref name="versionId"/>
    /// (as shown by <see cref="SnapshotHistoryService.GetFileHistory"/>) to
    /// <paramref name="destinationPath"/>. The <paramref name="overwrite"/>,
    /// <paramref name="onBytesCopied"/> and <paramref name="onSizeResolved"/> parameters behave
    /// exactly as for <see cref="RestoreAsOf"/>.
    /// </summary>
    /// <exception cref="NoHistoryForPathException">The path was never part of any recorded snapshot.</exception>
    /// <exception cref="NoMatchingVersionException">No such version id exists for the path.</exception>
    /// <exception cref="RestoreLinkedEntryException">The resolved version is a symlink/junction entry with no stored content.</exception>
    /// <exception cref="RestoreDestinationInMirrorException">The destination resolves inside the profile's live mirror.</exception>
    /// <exception cref="DestinationExistsException">
    /// The destination already exists and <paramref name="overwrite"/> is <c>false</c>.
    /// </exception>
    public void RestoreVersion(
        string relativePath,
        long versionId,
        string destinationPath,
        bool overwrite = false,
        Action<long>? onBytesCopied = null,
        Action<long>? onSizeResolved = null) =>
        Restore(ResolveVersion(relativePath, versionId, asOf: null), relativePath, destinationPath, overwrite, onBytesCopied, onSizeResolved);

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
    public ShowResult ShowVersion(string relativePath, long? versionId, DateTimeOffset? asOf, bool forceBinary = false)
    {
        var match = ResolveVersion(relativePath, versionId, asOf);
        if (match.ChangeKind == FileChangeKind.Linked)
        {
            return new ShowResult(new LinkedEntryShowRefusal(relativePath));
        }

        var source = contentStore.OpenRead(match.ContentHash!);
        try
        {
            if (!forceBinary && LooksBinary(source))
            {
                source.Dispose();
                return new ShowResult(new BinaryShowRefusal(relativePath));
            }

            return new ShowResult(source);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens two versions of the same path - each resolved by its own version id or "as of"
    /// date, independently, exactly as <see cref="RestoreVersion"/>/<see cref="RestoreAsOf"/>
    /// resolve theirs - for a caller to diff. The caller owns and disposes both streams. Refuses
    /// the comparison, before either side's full content is read, when either side's recorded
    /// size exceeds <see cref="MaxDiffContentSize"/> or either side's content is detected as
    /// binary from its leading <see cref="BinarySampleSize"/> bytes. Returns an explicit
    /// refusal result for those expected conditions rather than throwing.
    /// </summary>
    /// <exception cref="NoHistoryForPathException">The path was never part of any recorded snapshot.</exception>
    /// <exception cref="NoMatchingVersionException">No such version id/date exists for either side.</exception>
    public DiffResult OpenVersionsForDiff(
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
            return new DiffResult(new OversizedDiffRefusal(
                relativePath,
                MaxDiffContentSize,
                leftTooLarge ? left.Size : null,
                rightTooLarge ? right.Size : null));
        }

        var leftLinked = left.ChangeKind == FileChangeKind.Linked;
        var rightLinked = right.ChangeKind == FileChangeKind.Linked;
        if (leftLinked || rightLinked)
        {
            return new DiffResult(new LinkedEntryDiffRefusal(relativePath, leftLinked, rightLinked));
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
                leftStream.Dispose();
                rightStream.Dispose();
                leftStream = null;
                rightStream = null;
                return new DiffResult(new BinaryDiffRefusal(relativePath, leftIsBinary, rightIsBinary));
            }

            var streams = new DiffStreams(leftStream, rightStream);
            leftStream = null;
            rightStream = null;
            return new DiffResult(streams);
        }
        catch
        {
            leftStream?.Dispose();
            rightStream?.Dispose();
            throw;
        }
    }

    private void Restore(
        FileVersionRecord match,
        string relativePath,
        string destinationPath,
        bool overwrite,
        Action<long>? onBytesCopied,
        Action<long>? onSizeResolved)
    {
        GuardNotLinked(relativePath, match);
        GuardDestination(destinationPath, overwrite);
        onSizeResolved?.Invoke(match.Size);
        contentStore.ExtractTo(match.ContentHash!, destinationPath, onBytesCopied);
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

        return history.FirstOrDefault(r => r.RecordedAt <= asOf!.Value) is { ChangeKind: not FileChangeKind.DeletedChange } match
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
