using System.Diagnostics;
using Vara.Core.Abstractions;

namespace Vara.Application.Integrity;

/// <summary>
/// One missing or corrupt content blob, with the file path(s) that reference it so a
/// finding is actionable rather than just an opaque hash. <see cref="AffectedPaths"/>
/// is capped at a small number per finding (see design.md's "capping the listed paths
/// per hash" risk mitigation for heavily-deduplicated content) - <see cref="TotalAffectedPathCount"/>
/// is the true, uncapped count, so a caller can show a "+N more" summary when the list
/// was truncated.
/// </summary>
public sealed record IntegrityFinding(string ContentHash, IReadOnlyList<string> AffectedPaths, int TotalAffectedPathCount);

/// <summary>The outcome of an <see cref="IntegrityCheckService.Check"/> run.</summary>
/// <param name="BlobsChecked">Total number of content blobs referenced by the manifest that were examined.</param>
/// <param name="Missing">Referenced blobs not physically present in the target's content store.</param>
/// <param name="Corrupt">Referenced blobs present in the store but whose re-hashed content no longer matches the manifest. Always empty in quick mode, since quick mode never re-hashes.</param>
/// <param name="Orphaned">Blobs physically present in the store but not referenced by any snapshot - informational only; <see cref="IntegrityCheckService.Check"/> never deletes these (that remains <c>vara prune</c>'s job).</param>
/// <param name="Cancelled">
/// <see langword="true"/> when a graceful Ctrl+C stop was requested before verification of every
/// referenced blob completed, per the backup-integrity spec's "Graceful cancellation via Ctrl+C"
/// requirement. <see cref="BlobsChecked"/> and the finding lists reflect only what was actually
/// verified before the stop - already-found problems are not discarded on cancellation.
/// </param>
public sealed record IntegrityCheckResult(
    int BlobsChecked,
    IReadOnlyList<IntegrityFinding> Missing,
    IReadOnlyList<IntegrityFinding> Corrupt,
    IReadOnlyList<string> Orphaned,
    bool Cancelled = false);

/// <summary>
/// Reports blob-count, manifest-weighted byte, and observed timing progress for
/// <see cref="IntegrityCheckService.Check"/>.
/// </summary>
public sealed record IntegrityCheckProgress(
    int BlobsChecked,
    int TotalBlobs,
    long BytesChecked,
    long TotalBytes,
    long BytesRead,
    long RemainingPresentBytes,
    TimeSpan Elapsed,
    TimeSpan HashingElapsed);

/// <summary>
/// Verifies that content physically stored in a profile's target still matches what
/// the manifest expects, across the full referenced snapshot history (not only the
/// current live mirror) - see the backup-integrity spec. Strictly read-only: never
/// modifies the target, the manifest, or any stored content.
/// </summary>
public sealed class IntegrityCheckService(ISnapshotRepository repository, IContentStore contentStore, IHasher hasher)
{
    private const long ProgressByteInterval = 1024 * 1024;
    private static readonly TimeSpan ProgressTimeInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Cap on the number of affected paths attached per finding - see design.md's
    /// "capping the listed paths per hash" risk mitigation for content shared across
    /// many files/versions.
    /// </summary>
    private const int MaxAffectedPathsPerFinding = 5;

    /// <param name="quick">
    /// When <see langword="true"/>, only checks that each referenced blob is physically
    /// present in the store, without reading its content or recomputing its hash -
    /// trading full corruption detection for a faster presence-only check.
    /// </param>
    /// <param name="cancellationToken">
    /// Cooperative "stop starting new work" signal, set by a graceful Ctrl+C stop (see
    /// <c>Vara.Cli.Program</c>'s <c>Console.CancelKeyPress</c> handler), mirroring
    /// <see cref="Vara.Application.Backup.BackupPipeline.Run"/>'s own parameter. Checked between
    /// each referenced blob verified; whatever was already verified is reported normally - see
    /// the backup-integrity spec's "Graceful cancellation via Ctrl+C" requirement.
    /// </param>
    public IntegrityCheckResult Check(bool quick, IProgress<IntegrityCheckProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var referencedSizes = repository.GetAllReferencedContentSizes();
        var referencedHashes = referencedSizes.Keys.ToList();
        var storedHashes = contentStore.ListAllStoredHashes();
        var totalBytes = SumSizes(referencedSizes.Values);
        var totalPresentBytes = SumSizes(referencedSizes.Where(pair => storedHashes.Contains(pair.Key)).Select(pair => pair.Value));

        var missing = new List<IntegrityFinding>();
        var corrupt = new List<IntegrityFinding>();

        var blobsChecked = 0;
        long bytesChecked = 0;
        long bytesRead = 0;
        long completedPresentBytes = 0;
        var hashingElapsed = TimeSpan.Zero;
        var elapsed = Stopwatch.StartNew();
        ReportProgress();

        var cancelled = false;
        for (var i = 0; i < referencedHashes.Count; i++)
        {
            // Cooperative cancellation: stop starting verification of any further blob once a
            // graceful Ctrl+C stop has been requested. Whatever has already been verified is
            // allowed to finish (this loop is sequential, so nothing is "in flight" here beyond
            // the current iteration) - mirroring BackupExecutor's own cancellation checks.
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var hash = referencedHashes[i];
            var blobSize = Math.Max(0, referencedSizes[hash]);
            var blobBytesRead = 0L;

            if (!storedHashes.Contains(hash))
            {
                missing.Add(BuildFinding(hash));
            }
            else if (!quick)
            {
                using var content = contentStore.OpenRead(hash);
                var lastReportedBytes = 0L;
                var lastReportedAt = Stopwatch.GetTimestamp();
                var currentHashElapsed = TimeSpan.Zero;
                var hashTimer = Stopwatch.StartNew();
                using var countingContent = new CountingReadStream(content, read =>
                {
                    blobBytesRead = SaturatingAdd(blobBytesRead, read);
                    bytesRead = SaturatingAdd(bytesRead, read);

                    var now = Stopwatch.GetTimestamp();
                    if (blobBytesRead - lastReportedBytes >= ProgressByteInterval ||
                        Stopwatch.GetElapsedTime(lastReportedAt, now) >= ProgressTimeInterval)
                    {
                        lastReportedBytes = blobBytesRead;
                        lastReportedAt = now;
                        currentHashElapsed = hashTimer.Elapsed;
                        ReportProgress(blobSize, blobBytesRead, currentHashElapsed);
                    }
                });
                var actualHash = hasher.ComputeHash(countingContent);
                hashTimer.Stop();
                currentHashElapsed = TimeSpan.Zero;
                hashingElapsed += hashTimer.Elapsed;
                if (!string.Equals(actualHash, hash, StringComparison.Ordinal))
                {
                    corrupt.Add(BuildFinding(hash));
                }

                completedPresentBytes = SaturatingAdd(completedPresentBytes, blobSize);
            }
            else
            {
                completedPresentBytes = SaturatingAdd(completedPresentBytes, blobSize);
            }

            blobsChecked = i + 1;
            bytesChecked = SaturatingAdd(bytesChecked, blobSize);
            ReportProgress();
        }

        var orphaned = storedHashes.Except(referencedHashes).ToList();

        return new IntegrityCheckResult(blobsChecked, missing, corrupt, orphaned, Cancelled: cancelled);

        void ReportProgress(long hashSize = 0, long currentBlobBytesRead = 0, TimeSpan currentHashElapsed = default)
        {
            var creditedCurrentBytes = Math.Min(hashSize, Math.Max(0, currentBlobBytesRead));
            var progressBytes = SaturatingAdd(bytesChecked, creditedCurrentBytes);
            var remainingBytesToRead = Math.Max(0, totalPresentBytes - SaturatingAdd(completedPresentBytes, creditedCurrentBytes));

            progress?.Report(new IntegrityCheckProgress(
                blobsChecked,
                referencedHashes.Count,
                progressBytes,
                totalBytes,
                bytesRead,
                remainingBytesToRead,
                elapsed.Elapsed,
                hashingElapsed + currentHashElapsed));
        }
    }

    private static long SumSizes(IEnumerable<long> sizes)
    {
        var total = 0L;
        foreach (var size in sizes)
        {
            total = SaturatingAdd(total, Math.Max(0, size));
        }

        return total;
    }

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

    private IntegrityFinding BuildFinding(string hash)
    {
        var paths = repository.GetPathsForContentHash(hash);
        return new IntegrityFinding(hash, paths.Take(MaxAffectedPathsPerFinding).ToList(), paths.Count);
    }

    private sealed class CountingReadStream(Stream inner, Action<int> onRead) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                onRead(read);
            }

            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            if (read > 0)
            {
                onRead(read);
            }

            return read;
        }

        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value >= 0)
            {
                onRead(1);
            }

            return value;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
