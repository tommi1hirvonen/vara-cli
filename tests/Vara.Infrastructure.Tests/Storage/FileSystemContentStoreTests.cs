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
            Directory.Delete(_targetRoot, recursive: true);
        }
    }

    private static Stream Content(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

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
    public void RemoveFromMirror_deletes_the_mirror_entry_but_keeps_the_blob()
    {
        var store = CreateStore();
        store.ProbeHardlinkSupport();
        var (hash, _) = store.StoreFromStream(Content("removable content"));
        store.PlaceAtMirrorPath(hash, "file.txt");

        store.RemoveFromMirror("file.txt");

        Assert.False(File.Exists(Path.Combine(_targetRoot, "file.txt")));
        Assert.True(store.HasContent(hash));
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
}
