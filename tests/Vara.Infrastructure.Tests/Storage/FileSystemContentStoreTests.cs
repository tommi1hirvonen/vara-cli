using System.Text;
using Vara.Infrastructure.Hashing;
using Vara.Infrastructure.Storage;
using Xunit;

namespace Vara.Infrastructure.Tests.Storage;

public class FileSystemContentStoreTests : IDisposable
{
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-test-{Guid.NewGuid():N}");
    private readonly XxHash128Hasher _hasher = new();

    private FileSystemContentStore CreateStore() => new(_targetRoot, _hasher);

    public void Dispose()
    {
        if (Directory.Exists(_targetRoot))
        {
            // Hardlinked mirror entries are now marked read-only (protect-hardlinked-mirror-files),
            // and Directory.Delete(recursive: true) throws UnauthorizedAccessException against a
            // read-only file - clear the attribute on every file first so test cleanup can proceed.
            foreach (var file in Directory.EnumerateFiles(_targetRoot, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(_targetRoot, recursive: true);
        }
    }

    private static Stream Content(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Mirrors FileSystemContentStore's own private BlobPath(hash) convention, for tests
    /// that need to assert on the canonical blob file's attributes directly (there's already
    /// precedent for reaching into the internal `.vara\` layout in this test class - see
    /// CleanupOrphanedTemp_removes_leftover_staging_files's use of `.vara\tmp`).
    /// </summary>
    private string BlobPath(string hash) => Path.Combine(_targetRoot, ".vara", "versions", hash[..2], hash);

    private static Stream LargeContent(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return new MemoryStream(bytes);
    }

    [Fact]
    public void ProbeHardlinkSupport_succeeds_on_an_NTFS_path()
    {
        var store = CreateStore();

        var supported = store.ProbeHardlinkSupport();

        Assert.True(supported);
        Assert.True(store.SupportsHardlinks);
    }

    [Fact]
    public void SupportsHardlinks_throws_before_probing()
    {
        var store = CreateStore();

        Assert.Throws<InvalidOperationException>(() => store.SupportsHardlinks);
    }

    [Fact]
    public void StoreFromStream_computes_a_hash_and_reports_the_size()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();

        var (hash, size) = store.StoreFromStream(Content("hello world"));

        Assert.NotEmpty(hash);
        Assert.Equal(11, size);
        Assert.True(store.HasContent(hash));
    }

    [Fact]
    public void Storing_identical_content_twice_does_not_duplicate_the_blob()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();

        var (hash1, _) = store.StoreFromStream(Content("duplicate me"));
        var (hash2, _) = store.StoreFromStream(Content("duplicate me"));

        Assert.Equal(hash1, hash2);
        Assert.Single(store.ListAllStoredHashes());
    }

    [Fact]
    public void StoreFromStream_reports_incremental_progress_for_a_large_file()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        const int contentLength = 5 * 1024 * 1024;

        var reports = new List<long>();
        var (hash, size) = store.StoreFromStream(LargeContent(contentLength), bytes => reports.Add(bytes));

        Assert.Equal(contentLength, size);
        Assert.True(store.HasContent(hash));
        Assert.True(reports.Count > 1, $"expected more than one progress report for a large file, observed {reports.Count}");
        Assert.All(reports, bytes => Assert.True(bytes > 0));

        var cumulativeTotals = new List<long>();
        long running = 0;
        foreach (var chunk in reports)
        {
            running += chunk;
            cumulativeTotals.Add(running);
        }

        // Every reported chunk strictly grows the cumulative total, and the final total
        // equals the file's full size - progress never regresses and never over/under-counts.
        Assert.True(cumulativeTotals.SequenceEqual(cumulativeTotals.OrderBy(v => v)));
        Assert.Equal(contentLength, cumulativeTotals[^1]);
    }

    [Fact]
    public void PlaceAtMirrorPath_with_hardlink_support_places_a_hardlink()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("mirrored content"));

        store.PlaceAtMirrorPath(hash, @"Documents\report.txt");

        var mirrorPath = Path.Combine(_targetRoot, "Documents", "report.txt");
        Assert.True(File.Exists(mirrorPath));
        Assert.Equal("mirrored content", File.ReadAllText(mirrorPath));
    }

    [Fact]
    public void PlaceAtMirrorPath_without_hardlink_support_copies_the_content()
    {
        var store = CreateStore();
        store.ForceHardlinkSupportForTesting(false);
        var (hash, _) = store.StoreFromStream(Content("copied content"));

        store.PlaceAtMirrorPath(hash, @"Documents\report.txt");

        var mirrorPath = Path.Combine(_targetRoot, "Documents", "report.txt");
        Assert.True(File.Exists(mirrorPath));
        Assert.Equal("copied content", File.ReadAllText(mirrorPath));
    }

    [Fact]
    public void PlaceAtMirrorPath_without_hardlink_support_reports_incremental_progress_for_a_large_file()
    {
        var store = CreateStore();
        store.ForceHardlinkSupportForTesting(false);
        const int contentLength = 5 * 1024 * 1024;
        var (hash, _) = store.StoreFromStream(LargeContent(contentLength));

        var reports = new List<long>();
        store.PlaceAtMirrorPath(hash, "large.bin", bytes => reports.Add(bytes));

        Assert.True(reports.Count > 1, $"expected more than one progress report for a large fallback copy, observed {reports.Count}");
        Assert.Equal(contentLength, reports.Sum());
        Assert.Equal(contentLength, new FileInfo(Path.Combine(_targetRoot, "large.bin")).Length);
    }

    [Fact]
    public void PlaceAtMirrorPath_falls_back_to_a_copy_when_a_single_hardlink_attempt_fails()
    {
        // Simulates NTFS's 1024-hard-link-per-file cap being hit for this specific blob:
        // the volume otherwise supports hardlinks, but this one placement's link creation fails.
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("over-linked content"));
        store.ForceNextHardlinkFailureForTesting();

        store.PlaceAtMirrorPath(hash, @"Documents\report.txt");

        var mirrorPath = Path.Combine(_targetRoot, "Documents", "report.txt");
        Assert.True(File.Exists(mirrorPath));
        Assert.Equal("over-linked content", File.ReadAllText(mirrorPath));
    }

    [Fact]
    public void PlaceAtMirrorPath_falling_back_after_a_forced_hardlink_failure_reports_incremental_progress_for_a_large_file()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        const int contentLength = 5 * 1024 * 1024;
        var (hash, _) = store.StoreFromStream(LargeContent(contentLength));
        store.ForceNextHardlinkFailureForTesting();

        var reports = new List<long>();
        store.PlaceAtMirrorPath(hash, "large.bin", bytes => reports.Add(bytes));

        Assert.True(reports.Count > 1, $"expected more than one progress report for a large fallback copy, observed {reports.Count}");
        Assert.Equal(contentLength, reports.Sum());
        Assert.Equal(contentLength, new FileInfo(Path.Combine(_targetRoot, "large.bin")).Length);
    }

    [Fact]
    public void PlaceAtMirrorPath_attempts_a_hardlink_again_after_a_prior_forced_failure()
    {
        // A blob's hard-link count isn't a stable, cacheable fact (unlike volume-level hardlink
        // support): confirms the store doesn't remember a prior failure and always retries a
        // hardlink first for the next placement.
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("recovering content"));
        store.ForceNextHardlinkFailureForTesting();
        store.PlaceAtMirrorPath(hash, "first.txt");

        // The forced failure was single-shot, so this placement should succeed via a real
        // hardlink rather than another forced or memoized copy fallback.
        store.PlaceAtMirrorPath(hash, "second.txt");

        var firstPath = Path.Combine(_targetRoot, "first.txt");
        var secondPath = Path.Combine(_targetRoot, "second.txt");
        Assert.Equal("recovering content", File.ReadAllText(firstPath));
        Assert.Equal("recovering content", File.ReadAllText(secondPath));
    }

    [Fact]
    public void PlaceAtMirrorPath_atomically_replaces_an_existing_mirror_entry()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (oldHash, _) = store.StoreFromStream(Content("old content"));
        store.PlaceAtMirrorPath(oldHash, "file.txt");

        var (newHash, _) = store.StoreFromStream(Content("new content"));
        store.PlaceAtMirrorPath(newHash, "file.txt");

        Assert.Equal("new content", File.ReadAllText(Path.Combine(_targetRoot, "file.txt")));
        // The old content must still be retrievable from the store (history is preserved).
        Assert.True(store.HasContent(oldHash));
    }

    [Fact]
    public void PlaceAtMirrorPath_with_hardlink_support_marks_the_mirror_file_read_only()
    {
        // A hardlinked mirror file IS the content-store blob (same physical file): read-only
        // turns a naive in-place overwrite into a failure instead of silently corrupting the
        // blob, its version history, and every other mirror path sharing the same content.
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("mirrored content"));

        store.PlaceAtMirrorPath(hash, @"Documents\report.txt");

        var mirrorPath = Path.Combine(_targetRoot, "Documents", "report.txt");
        Assert.True(File.GetAttributes(mirrorPath).HasFlag(FileAttributes.ReadOnly));
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(mirrorPath, "tampered"));
    }

    [Fact]
    public void PlaceAtMirrorPath_without_hardlink_support_leaves_the_mirror_file_writable()
    {
        var store = CreateStore();
        store.ForceHardlinkSupportForTesting(false);
        var (hash, _) = store.StoreFromStream(Content("copied content"));

        store.PlaceAtMirrorPath(hash, @"Documents\report.txt");

        var mirrorPath = Path.Combine(_targetRoot, "Documents", "report.txt");
        Assert.False(File.GetAttributes(mirrorPath).HasFlag(FileAttributes.ReadOnly));
        File.WriteAllText(mirrorPath, "edited in place");
        Assert.Equal("edited in place", File.ReadAllText(mirrorPath));
    }

    [Fact]
    public void PlaceAtMirrorPath_falling_back_to_a_copy_leaves_the_mirror_file_writable()
    {
        // Same as the no-hardlink-support case, but reached via the per-blob hardlink-limit
        // fallback instead - a copy-fallback entry is an independent physical copy, whichever
        // reason triggered the fallback.
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("over-linked content"));
        store.ForceNextHardlinkFailureForTesting();

        store.PlaceAtMirrorPath(hash, @"Documents\report.txt");

        var mirrorPath = Path.Combine(_targetRoot, "Documents", "report.txt");
        Assert.False(File.GetAttributes(mirrorPath).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void PlaceAtMirrorPath_transitioning_from_hardlink_to_copy_fallback_clears_the_read_only_attribute()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("transitioning content"));
        store.PlaceAtMirrorPath(hash, "file.txt");
        var mirrorPath = Path.Combine(_targetRoot, "file.txt");
        Assert.True(File.GetAttributes(mirrorPath).HasFlag(FileAttributes.ReadOnly));

        // Simulates the blob's hard-link limit being reached on a later run.
        store.ForceNextHardlinkFailureForTesting();
        store.PlaceAtMirrorPath(hash, "file.txt");

        Assert.False(File.GetAttributes(mirrorPath).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal("transitioning content", File.ReadAllText(mirrorPath));
    }

    [Fact]
    public void PlaceAtMirrorPath_transitioning_from_copy_fallback_to_hardlink_sets_the_read_only_attribute()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("recovering content"));
        store.ForceNextHardlinkFailureForTesting();
        store.PlaceAtMirrorPath(hash, "file.txt");
        var mirrorPath = Path.Combine(_targetRoot, "file.txt");
        Assert.False(File.GetAttributes(mirrorPath).HasFlag(FileAttributes.ReadOnly));

        // The forced failure was single-shot, so this placement succeeds via a real hardlink.
        store.PlaceAtMirrorPath(hash, "file.txt");

        Assert.True(File.GetAttributes(mirrorPath).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal("recovering content", File.ReadAllText(mirrorPath));
    }

    [Fact]
    public void PlaceAtMirrorPath_overwriting_an_already_hardlinked_entry_succeeds_and_remains_read_only()
    {
        // Regression coverage: File.Move(overwrite: true) throws UnauthorizedAccessException
        // against a read-only destination unless it is explicitly cleared first - this is the
        // ordinary "Changed" file backup scenario for a file that was already hardlinked once.
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (oldHash, _) = store.StoreFromStream(Content("old content"));
        store.PlaceAtMirrorPath(oldHash, "file.txt");
        var mirrorPath = Path.Combine(_targetRoot, "file.txt");
        Assert.True(File.GetAttributes(mirrorPath).HasFlag(FileAttributes.ReadOnly));

        var (newHash, _) = store.StoreFromStream(Content("new content"));
        store.PlaceAtMirrorPath(newHash, "file.txt", previousContentHash: oldHash);

        Assert.Equal("new content", File.ReadAllText(mirrorPath));
        Assert.True(File.GetAttributes(mirrorPath).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void PlaceAtMirrorPath_re_placing_the_same_content_with_no_previous_hash_stays_read_only()
    {
        // Regression coverage: this simulates a run recovering from an interruption that wrote
        // the mirror entry but never recorded it in the manifest, so the planner treats the
        // path as a fresh "Add" with no previousContentHash to restore protection from. The
        // mirror path is already a hardlink to the same blob as the incoming content, so the
        // staged file, the mirror path, and the blob are all one underlying file - clearing
        // read-only on the destination before the move must not permanently strip it from the
        // shared blob.
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("stable content"));
        store.PlaceAtMirrorPath(hash, "file.txt");
        var mirrorPath = Path.Combine(_targetRoot, "file.txt");
        Assert.True(File.GetAttributes(mirrorPath).HasFlag(FileAttributes.ReadOnly));

        store.PlaceAtMirrorPath(hash, "file.txt");

        Assert.True(File.GetAttributes(mirrorPath).HasFlag(FileAttributes.ReadOnly));
        Assert.True(File.GetAttributes(BlobPath(hash)).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void ExtractTo_writes_the_blobs_content_to_a_fresh_destination()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("extracted content"));
        var destination = Path.Combine(Path.GetTempPath(), $"vara-extract-{Guid.NewGuid():N}.txt");

        try
        {
            store.ExtractTo(hash, destination);

            Assert.Equal("extracted content", File.ReadAllText(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void ExtractTo_overwrites_an_existing_destination_file()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("new extracted content"));
        var destination = Path.Combine(Path.GetTempPath(), $"vara-extract-{Guid.NewGuid():N}.txt");
        File.WriteAllText(destination, "stale content that must be replaced");

        try
        {
            store.ExtractTo(hash, destination);

            Assert.Equal("new extracted content", File.ReadAllText(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void ExtractTo_reports_incremental_progress_for_a_large_file()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        const int contentLength = 5 * 1024 * 1024;
        var (hash, _) = store.StoreFromStream(LargeContent(contentLength));
        var destination = Path.Combine(Path.GetTempPath(), $"vara-extract-{Guid.NewGuid():N}.txt");

        try
        {
            var reports = new List<long>();
            store.ExtractTo(hash, destination, bytes => reports.Add(bytes));

            Assert.True(reports.Count > 1, $"expected more than one progress report for a large file, observed {reports.Count}");
            Assert.Equal(contentLength, reports.Sum());
            Assert.Equal(contentLength, new FileInfo(destination).Length);
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void RemoveExtractedFile_deletes_an_existing_file()
    {
        var store = CreateStore();
        var destination = Path.Combine(Path.GetTempPath(), $"vara-extract-{Guid.NewGuid():N}.txt");
        File.WriteAllText(destination, "content to remove");

        store.RemoveExtractedFile(destination);

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void RemoveExtractedFile_is_a_no_op_for_a_path_that_does_not_exist()
    {
        var store = CreateStore();
        var destination = Path.Combine(Path.GetTempPath(), $"vara-extract-{Guid.NewGuid():N}.txt");

        var exception = Record.Exception(() => store.RemoveExtractedFile(destination));

        Assert.Null(exception);
    }

    [Fact]
    public void OpenRead_returns_a_readable_stream_over_a_stored_hashs_content()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("readable content"));

        using var stream = store.OpenRead(hash);
        using var reader = new StreamReader(stream);

        Assert.Equal("readable content", reader.ReadToEnd());
    }

    [Fact]
    public void OpenRead_throws_for_a_hash_with_no_stored_content()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();

        Assert.Throws<FileNotFoundException>(() => store.OpenRead("nonexistent-hash"));
    }

    [Fact]
    public void MoveMirrorEntry_relocates_the_file_without_touching_its_content()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("movable content"));
        store.PlaceAtMirrorPath(hash, @"Downloads\report.pdf");

        store.MoveMirrorEntry(@"Downloads\report.pdf", @"Documents\report.pdf");

        Assert.False(File.Exists(Path.Combine(_targetRoot, "Downloads", "report.pdf")));
        var newPath = Path.Combine(_targetRoot, "Documents", "report.pdf");
        Assert.True(File.Exists(newPath));
        Assert.Equal("movable content", File.ReadAllText(newPath));
    }

    [Fact]
    public void MoveMirrorEntry_succeeds_on_a_read_only_hardlinked_entry_and_stays_read_only()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("movable content"));
        store.PlaceAtMirrorPath(hash, @"Downloads\report.pdf");
        var oldPath = Path.Combine(_targetRoot, "Downloads", "report.pdf");
        Assert.True(File.GetAttributes(oldPath).HasFlag(FileAttributes.ReadOnly));

        store.MoveMirrorEntry(@"Downloads\report.pdf", @"Documents\report.pdf");

        var newPath = Path.Combine(_targetRoot, "Documents", "report.pdf");
        Assert.True(File.Exists(newPath));
        Assert.True(File.GetAttributes(newPath).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void MoveMirrorEntry_retried_after_the_destination_already_exists_is_a_no_op()
    {
        // Simulates a run interrupted between the mirror rename and its manifest commit:
        // the physical relocation already happened, so the retry must not throw.
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("movable content"));
        store.PlaceAtMirrorPath(hash, @"Downloads\report.pdf");
        store.MoveMirrorEntry(@"Downloads\report.pdf", @"Documents\report.pdf");

        store.MoveMirrorEntry(@"Downloads\report.pdf", @"Documents\report.pdf");

        Assert.False(File.Exists(Path.Combine(_targetRoot, "Downloads", "report.pdf")));
        var newPath = Path.Combine(_targetRoot, "Documents", "report.pdf");
        Assert.True(File.Exists(newPath));
        Assert.Equal("movable content", File.ReadAllText(newPath));
    }

    [Fact]
    public void MoveMirrorEntry_throws_when_neither_source_nor_destination_exists()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();

        Assert.Throws<FileNotFoundException>(() =>
            store.MoveMirrorEntry(@"Downloads\missing.pdf", @"Documents\missing.pdf"));
    }

    [Fact]
    public void MoveMirrorEntry_onto_an_occupied_read_only_destination_succeeds_and_stays_read_only()
    {
        // Regression coverage: the archived design assumed MoveMirrorEntry always targets a
        // not-yet-existing destination, but the method's own doc comment describes the
        // interrupted-run case where the destination already exists - and once mirror entries
        // became read-only, File.Move(overwrite: true) against that occupied, read-only
        // destination started throwing UnauthorizedAccessException.
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (sourceHash, _) = store.StoreFromStream(Content("source content"));
        var (destinationHash, _) = store.StoreFromStream(Content("destination content"));
        store.PlaceAtMirrorPath(sourceHash, @"Downloads\report.pdf");
        store.PlaceAtMirrorPath(destinationHash, @"Documents\report.pdf");
        var destinationPath = Path.Combine(_targetRoot, "Documents", "report.pdf");
        Assert.True(File.GetAttributes(destinationPath).HasFlag(FileAttributes.ReadOnly));

        store.MoveMirrorEntry(@"Downloads\report.pdf", @"Documents\report.pdf");

        Assert.False(File.Exists(Path.Combine(_targetRoot, "Downloads", "report.pdf")));
        Assert.Equal("source content", File.ReadAllText(destinationPath));
        Assert.True(File.GetAttributes(destinationPath).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void MoveMirrorEntry_of_a_writable_copy_fallback_entry_onto_an_occupied_destination_stays_writable()
    {
        // A move must carry its entry's own attribute forward as-is, not upgrade a
        // copy-fallback (writable) entry into a read-only one just because it happens to
        // overwrite a previously read-only destination.
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (sourceHash, _) = store.StoreFromStream(Content("source content"));
        var (destinationHash, _) = store.StoreFromStream(Content("destination content"));
        store.ForceNextHardlinkFailureForTesting();
        store.PlaceAtMirrorPath(sourceHash, @"Downloads\report.pdf");
        store.PlaceAtMirrorPath(destinationHash, @"Documents\report.pdf");
        var sourcePath = Path.Combine(_targetRoot, "Downloads", "report.pdf");
        var destinationPath = Path.Combine(_targetRoot, "Documents", "report.pdf");
        Assert.False(File.GetAttributes(sourcePath).HasFlag(FileAttributes.ReadOnly));
        Assert.True(File.GetAttributes(destinationPath).HasFlag(FileAttributes.ReadOnly));

        store.MoveMirrorEntry(@"Downloads\report.pdf", @"Documents\report.pdf");

        Assert.False(File.Exists(sourcePath));
        Assert.Equal("source content", File.ReadAllText(destinationPath));
        Assert.False(File.GetAttributes(destinationPath).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void RemoveFromMirror_deletes_the_mirror_entry_but_keeps_the_blob()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("removable content"));
        store.PlaceAtMirrorPath(hash, "file.txt");

        store.RemoveFromMirror("file.txt", hash);

        Assert.False(File.Exists(Path.Combine(_targetRoot, "file.txt")));
        Assert.True(store.HasContent(hash));
    }

    [Fact]
    public void RemoveFromMirror_succeeds_on_a_read_only_hardlinked_entry()
    {
        // Regression coverage: File.Delete throws UnauthorizedAccessException against a
        // read-only target unless it is explicitly cleared first.
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("removable content"));
        store.PlaceAtMirrorPath(hash, "file.txt");
        var mirrorPath = Path.Combine(_targetRoot, "file.txt");
        Assert.True(File.GetAttributes(mirrorPath).HasFlag(FileAttributes.ReadOnly));

        store.RemoveFromMirror("file.txt", hash);

        Assert.False(File.Exists(mirrorPath));
        Assert.True(store.HasContent(hash));
    }

    [Fact]
    public void RemoveFromMirror_restores_read_only_on_the_blob_after_clearing_it_to_delete()
    {
        // The clear-before-delete step needed to remove a read-only hardlinked mirror entry
        // shares its attribute with the canonical blob file (NTFS ties FileAttributes.ReadOnly
        // to the underlying file, not the specific hardlink name) - this asserts the blob's own
        // attribute is restored afterward rather than left permanently writable.
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("removable content"));
        store.PlaceAtMirrorPath(hash, "file.txt");

        store.RemoveFromMirror("file.txt", hash);

        Assert.True(File.GetAttributes(BlobPath(hash)).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void RemoveFromMirror_does_not_weaken_protection_on_a_deduplicated_sibling_mirror_path()
    {
        // Two mirror paths hardlinked to the same content share one underlying file on NTFS -
        // deleting one must not leave the other permanently writable.
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("shared content"));
        store.PlaceAtMirrorPath(hash, "first.txt");
        store.PlaceAtMirrorPath(hash, "second.txt");
        var secondPath = Path.Combine(_targetRoot, "second.txt");
        Assert.True(File.GetAttributes(secondPath).HasFlag(FileAttributes.ReadOnly));

        store.RemoveFromMirror("first.txt", hash);

        Assert.True(File.GetAttributes(secondPath).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void PlaceAtMirrorPath_does_not_weaken_protection_on_a_deduplicated_sibling_mirror_path_after_an_overwrite()
    {
        // Same shared-attribute hazard as the RemoveFromMirror case, but for the "Changed
        // file" overwrite path: changing one deduplicated mirror path's content must not
        // leave a sibling mirror path (still referencing the old content) permanently writable.
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (oldHash, _) = store.StoreFromStream(Content("shared content"));
        store.PlaceAtMirrorPath(oldHash, "first.txt");
        store.PlaceAtMirrorPath(oldHash, "second.txt");
        var secondPath = Path.Combine(_targetRoot, "second.txt");
        Assert.True(File.GetAttributes(secondPath).HasFlag(FileAttributes.ReadOnly));

        var (newHash, _) = store.StoreFromStream(Content("new content"));
        store.PlaceAtMirrorPath(newHash, "first.txt", previousContentHash: oldHash);

        Assert.True(File.GetAttributes(secondPath).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void DeleteContent_removes_the_blob_from_the_store()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("garbage collected content"));

        store.DeleteContent(hash);

        Assert.False(store.HasContent(hash));
    }

    [Fact]
    public void DeleteContent_succeeds_on_a_blob_that_has_become_read_only()
    {
        // Hardlinking a blob into the mirror (and the RemoveFromMirror/PlaceAtMirrorPath
        // restore steps) can leave the blob itself read-only - permanent GC deletion must
        // still succeed rather than throwing UnauthorizedAccessException.
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("garbage collected content"));
        store.PlaceAtMirrorPath(hash, "file.txt");
        store.RemoveFromMirror("file.txt", hash);
        Assert.True(File.GetAttributes(BlobPath(hash)).HasFlag(FileAttributes.ReadOnly));

        store.DeleteContent(hash);

        Assert.False(store.HasContent(hash));
    }

    [Fact]
    public void CleanupOrphanedTemp_removes_leftover_staging_files()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var tempDir = Path.Combine(_targetRoot, ".vara", "tmp");
        var orphan = Path.Combine(tempDir, "leftover.tmp");
        File.WriteAllText(orphan, "leftover from an interrupted run");

        store.CleanupOrphanedTemp();

        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public void ListAllStoredHashes_enumerates_every_physically_stored_blob()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash1, _) = store.StoreFromStream(Content("content one"));
        var (hash2, _) = store.StoreFromStream(Content("content two"));

        var stored = store.ListAllStoredHashes();

        Assert.Equal(new HashSet<string> { hash1, hash2 }, stored);
    }

    [Fact]
    public void Concurrent_StoreFromStream_calls_for_identical_content_do_not_fail()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();

        var results = new string[16];
        Parallel.For(0, 16, i => results[i] = store.StoreFromStream(Content("same content from many threads")).Hash);

        Assert.All(results, h => Assert.Equal(results[0], h));
        Assert.Single(store.ListAllStoredHashes());
    }

    [Fact]
    public void IsWithinMirror_reports_true_for_a_path_inside_the_mirror_root()
    {
        var store = CreateStore();

        Assert.True(store.IsWithinMirror(Path.Combine(_targetRoot, "sub", "file.txt")));
    }

    [Fact]
    public void IsWithinMirror_reports_true_for_the_mirror_root_itself()
    {
        var store = CreateStore();

        Assert.True(store.IsWithinMirror(_targetRoot));
    }

    [Fact]
    public void IsWithinMirror_reports_false_for_a_path_outside_the_mirror_root()
    {
        var store = CreateStore();
        var outsidePath = Path.Combine(Path.GetTempPath(), $"vara-outside-{Guid.NewGuid():N}", "file.txt");

        Assert.False(store.IsWithinMirror(outsidePath));
    }

    [Fact]
    public void IsWithinMirror_reports_false_for_a_sibling_whose_name_merely_starts_with_the_mirror_roots_name()
    {
        var store = CreateStore();
        var siblingWithSharedPrefix = _targetRoot + "-sibling";

        Assert.False(store.IsWithinMirror(Path.Combine(siblingWithSharedPrefix, "file.txt")));
    }

    [Fact]
    public void TargetExists_reports_true_for_an_existing_file()
    {
        var store = CreateStore();
        var path = Path.Combine(Path.GetTempPath(), $"vara-existing-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "content");

        try
        {
            Assert.True(store.TargetExists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TargetExists_reports_false_for_a_non_existing_file()
    {
        var store = CreateStore();
        var path = Path.Combine(Path.GetTempPath(), $"vara-missing-{Guid.NewGuid():N}.txt");

        Assert.False(store.TargetExists(path));
    }

    // fix-readonly-command-side-effects: createIfMissing behavior.

    [Fact]
    public void Default_constructor_still_eagerly_creates_the_vara_directories()
    {
        Assert.False(Directory.Exists(_targetRoot));

        _ = CreateStore();

        Assert.True(Directory.Exists(_targetRoot));
        Assert.True(Directory.Exists(Path.Combine(_targetRoot, ".vara", "versions")));
        Assert.True(Directory.Exists(Path.Combine(_targetRoot, ".vara", "tmp")));
    }

    [Fact]
    public void CreateIfMissing_false_does_not_create_any_directory_when_absent()
    {
        Assert.False(Directory.Exists(_targetRoot));

        _ = new FileSystemContentStore(_targetRoot, _hasher, createIfMissing: false);

        Assert.False(Directory.Exists(_targetRoot));
        Assert.False(Directory.Exists(Path.Combine(_targetRoot, ".vara")));
    }
}
