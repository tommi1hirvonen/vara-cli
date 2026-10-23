using Vara.Application.History;
using Vara.Application.Tests.Backup;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Application.Tests.History;

public class FileVersionServiceTests
{
    private static Stream GetShowStream(ShowResult result) => result switch
    {
        Stream stream => stream,
        ShowRefusal refusal => throw new Xunit.Sdk.XunitException($"Expected shown content but received refusal: {refusal.Message}"),
    };

    private static ShowRefusal GetShowRefusal(ShowResult result) => result switch
    {
        Stream => throw new Xunit.Sdk.XunitException("Expected a show refusal but received content."),
        ShowRefusal refusal => refusal,
    };

    private static DiffStreams GetDiffStreams(DiffResult result) => result switch
    {
        DiffStreams streams => streams,
        DiffRefusal refusal => throw new Xunit.Sdk.XunitException($"Expected diff streams but received refusal: {refusal.Message}"),
    };

    private static DiffRefusal GetDiffRefusal(DiffResult result) => result switch
    {
        DiffStreams => throw new Xunit.Sdk.XunitException("Expected a diff refusal but received streams."),
        DiffRefusal refusal => refusal,
    };

    [Fact]
    public void RestoreAsOf_extracts_the_content_current_at_that_date()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("old content"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 11, t0, FileChangeKind.Added, t0);
        var service = new FileVersionService(repository, contentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-restore-{Guid.NewGuid():N}.txt");

        try
        {
            service.RestoreAsOf("a.txt", t0.AddDays(1), destination);

            Assert.Equal("old content", File.ReadAllText(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void RestoreAsOf_for_an_untracked_path_throws_NoHistoryForPath()
    {
        var service = new FileVersionService(new FakeSnapshotRepository(), new FakeContentStore());

        Assert.Throws<NoHistoryForPathException>(() => service.RestoreAsOf("never.txt", DateTimeOffset.UtcNow, "out.txt"));
    }

    [Fact]
    public void RestoreAsOf_before_the_path_existed_throws_NoMatchingVersion()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var service = new FileVersionService(repository, new FakeContentStore());

        Assert.Throws<NoMatchingVersionException>(() => service.RestoreAsOf("a.txt", t0.AddDays(-1), "out.txt"));
    }

    [Fact]
    public void RestoreAsOf_after_a_deletion_throws_NoMatchingVersion()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, "hash-1", 10, t1, FileChangeKind.Deleted, t1);
        var service = new FileVersionService(repository, new FakeContentStore());

        Assert.Throws<NoMatchingVersionException>(() => service.RestoreAsOf("a.txt", t1.AddDays(1), "out.txt"));
    }

    [Fact]
    public void RestoreAsOf_a_deleted_file_before_its_deletion_still_restores_it()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("still here"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hash, 10, t1, FileChangeKind.Deleted, t1);
        var service = new FileVersionService(repository, contentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-restore-{Guid.NewGuid():N}.txt");

        try
        {
            service.RestoreAsOf("a.txt", t0.AddHours(12), destination);

            Assert.Equal("still here", File.ReadAllText(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void RestoreVersion_extracts_the_content_of_the_specific_version_id()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("versioned content"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 17, t0, FileChangeKind.Added, t0);
        var versionId = repository.GetFileHistory("a.txt").Single().Id;
        var service = new FileVersionService(repository, contentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-restore-{Guid.NewGuid():N}.txt");

        try
        {
            service.RestoreVersion("a.txt", versionId, destination);

            Assert.Equal("versioned content", File.ReadAllText(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void RestoreAsOf_invokes_onBytesCopied_incrementally_and_onSizeResolved_with_the_matched_versions_size()
    {
        var contentStore = new FakeContentStore();
        var largeContent = new byte[64 * 1024];
        Random.Shared.NextBytes(largeContent);
        var (hash, size) = contentStore.StoreFromStream(new MemoryStream(largeContent));
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, size, t0, FileChangeKind.Added, t0);
        var service = new FileVersionService(repository, contentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-restore-{Guid.NewGuid():N}.txt");

        try
        {
            var chunkReports = new List<long>();
            long? resolvedSize = null;

            service.RestoreAsOf(
                "a.txt",
                t0.AddDays(1),
                destination,
                onBytesCopied: bytes => chunkReports.Add(bytes),
                onSizeResolved: resolved => resolvedSize = resolved);

            Assert.True(
                chunkReports.Count > 1,
                $"expected more than one progress report for a multi-chunk file, observed {chunkReports.Count}");
            Assert.Equal(size, chunkReports.Sum());
            Assert.Equal(size, resolvedSize);
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void RestoreVersion_with_an_unknown_id_throws_NoMatchingVersion()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, "a.txt", null, "hash", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var service = new FileVersionService(repository, new FakeContentStore());

        Assert.Throws<NoMatchingVersionException>(() => service.RestoreVersion("a.txt", 9999, "out.txt"));
    }

    [Fact]
    public void RestoreVersion_of_a_linked_entry_throws_RestoreLinkedEntry_instead_of_extracting()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "link", null, null, 0, t0, FileChangeKind.Linked, t0, linkTarget: @"C:\target");
        var versionId = repository.GetFileHistory("link").Single().Id;
        var service = new FileVersionService(repository, new FakeContentStore());

        var ex = Assert.Throws<RestoreLinkedEntryException>(() => service.RestoreVersion("link", versionId, "out.txt"));

        Assert.Equal("link", ex.RelativePath);
    }

    [Fact]
    public void RestoreAsOf_of_a_linked_entry_throws_RestoreLinkedEntry_instead_of_extracting()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "link", null, null, 0, t0, FileChangeKind.Linked, t0, linkTarget: @"C:\target");
        var service = new FileVersionService(repository, new FakeContentStore());

        var ex = Assert.Throws<RestoreLinkedEntryException>(() => service.RestoreAsOf("link", t0.AddDays(1), "out.txt"));

        Assert.Equal("link", ex.RelativePath);
    }

    [Fact]
    public void RestoreAsOf_to_a_destination_within_the_mirror_throws_even_when_overwrite_is_true()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("content"u8.ToArray()));
        contentStore.MirrorPaths.Add(@"C:\mirror\a.txt");
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 7, t0, FileChangeKind.Added, t0);
        var service = new FileVersionService(repository, contentStore);

        var ex = Assert.Throws<RestoreDestinationInMirrorException>(
            () => service.RestoreAsOf("a.txt", t0.AddDays(1), @"C:\mirror\a.txt", overwrite: true));
        Assert.Equal(@"C:\mirror\a.txt", ex.DestinationPath);
    }

    [Fact]
    public void RestoreAsOf_to_an_existing_destination_without_overwrite_throws_DestinationExists()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("content"u8.ToArray()));
        contentStore.SeedExistingTarget("out.txt");
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 7, t0, FileChangeKind.Added, t0);
        var service = new FileVersionService(repository, contentStore);

        var ex = Assert.Throws<DestinationExistsException>(() => service.RestoreAsOf("a.txt", t0.AddDays(1), "out.txt"));
        Assert.Equal("out.txt", ex.DestinationPath);
    }

    [Fact]
    public void RestoreAsOf_to_an_existing_destination_with_overwrite_true_overwrites_it()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("new content"u8.ToArray()));
        contentStore.SeedExistingTarget("out.txt");
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 11, t0, FileChangeKind.Added, t0);
        var service = new FileVersionService(repository, contentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-restore-{Guid.NewGuid():N}.txt");
        contentStore.SeedExistingTarget(destination);

        try
        {
            service.RestoreAsOf("a.txt", t0.AddDays(1), destination, overwrite: true);

            Assert.Equal("new content", File.ReadAllText(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void RestoreVersion_to_a_destination_within_the_mirror_throws_even_when_overwrite_is_true()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("content"u8.ToArray()));
        contentStore.MirrorPaths.Add(@"C:\mirror\a.txt");
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 7, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var versionId = repository.GetFileHistory("a.txt").Single().Id;
        var service = new FileVersionService(repository, contentStore);

        var ex = Assert.Throws<RestoreDestinationInMirrorException>(
            () => service.RestoreVersion("a.txt", versionId, @"C:\mirror\a.txt", overwrite: true));
        Assert.Equal(@"C:\mirror\a.txt", ex.DestinationPath);
    }

    [Fact]
    public void RestoreVersion_to_an_existing_destination_without_overwrite_throws_DestinationExists()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("content"u8.ToArray()));
        contentStore.SeedExistingTarget("out.txt");
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 7, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var versionId = repository.GetFileHistory("a.txt").Single().Id;
        var service = new FileVersionService(repository, contentStore);

        var ex = Assert.Throws<DestinationExistsException>(() => service.RestoreVersion("a.txt", versionId, "out.txt"));
        Assert.Equal("out.txt", ex.DestinationPath);
    }

    [Fact]
    public void RestoreVersion_to_an_existing_destination_with_overwrite_true_overwrites_it()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("versioned content"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 17, t0, FileChangeKind.Added, t0);
        var versionId = repository.GetFileHistory("a.txt").Single().Id;
        var service = new FileVersionService(repository, contentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-restore-{Guid.NewGuid():N}.txt");
        contentStore.SeedExistingTarget(destination);

        try
        {
            service.RestoreVersion("a.txt", versionId, destination, overwrite: true);

            Assert.Equal("versioned content", File.ReadAllText(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void ShowVersion_writes_the_resolved_versions_content_to_the_destination_stream()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("shown content"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 13, t0, FileChangeKind.Added, t0);
        var versionId = repository.GetFileHistory("a.txt").Single().Id;
        var service = new FileVersionService(repository, contentStore);
        using var destination = new MemoryStream();

        using var shown = GetShowStream(service.ShowVersion("a.txt", versionId, asOf: null));
        shown.CopyTo(destination);

        Assert.Equal("shown content", System.Text.Encoding.UTF8.GetString(destination.ToArray()));
    }

    [Fact]
    public void ShowVersion_for_an_untracked_path_throws_NoHistoryForPath()
    {
        var service = new FileVersionService(new FakeSnapshotRepository(), new FakeContentStore());

        Assert.Throws<NoHistoryForPathException>(() => service.ShowVersion("never.txt", 1, null));
    }

    [Fact]
    public void ShowVersion_with_an_unknown_version_id_throws_NoMatchingVersion()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, "a.txt", null, "hash", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var service = new FileVersionService(repository, new FakeContentStore());

        Assert.Throws<NoMatchingVersionException>(() => service.ShowVersion("a.txt", 9999, null));
    }

    [Fact]
    public void ShowVersion_with_binary_content_and_forceBinary_false_throws_before_writing_any_bytes()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream(BinaryContent(nulOffset: 5, totalLength: 20)));
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.bin", null, hash, 20, t0, FileChangeKind.Added, t0);
        var versionId = repository.GetFileHistory("a.bin").Single().Id;
        var service = new FileVersionService(repository, contentStore);
        using var destination = new MemoryStream();

        Assert.IsType<BinaryShowRefusal>(GetShowRefusal(service.ShowVersion("a.bin", versionId, asOf: null, forceBinary: false)));

        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public void ShowVersion_with_binary_content_and_forceBinary_true_streams_it_exactly_as_before()
    {
        var contentStore = new FakeContentStore();
        var content = BinaryContent(nulOffset: 5, totalLength: 20);
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream(content));
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.bin", null, hash, 20, t0, FileChangeKind.Added, t0);
        var versionId = repository.GetFileHistory("a.bin").Single().Id;
        var service = new FileVersionService(repository, contentStore);
        using var destination = new MemoryStream();

        using var shown = GetShowStream(service.ShowVersion("a.bin", versionId, asOf: null, forceBinary: true));
        shown.CopyTo(destination);

        Assert.Equal(content, destination.ToArray());
    }

    [Fact]
    public void OpenVersionsForDiff_resolves_each_side_independently()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, 11, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 11, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new FileVersionService(repository, contentStore);

        using var streams = GetDiffStreams(service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null));
        var (left, right) = (streams.Left, streams.Right);
        using var leftReader = new StreamReader(left);
        using var rightReader = new StreamReader(right);

        Assert.Equal("version one", leftReader.ReadToEnd());
        Assert.Equal("version two", rightReader.ReadToEnd());
    }

    [Fact]
    public void OpenVersionsForDiff_with_an_unknown_version_on_either_side_throws_NoMatchingVersion()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, "a.txt", null, "hash", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var versionId = repository.GetFileHistory("a.txt").Single().Id;
        var service = new FileVersionService(repository, new FakeContentStore());

        Assert.Throws<NoMatchingVersionException>(() => service.OpenVersionsForDiff("a.txt", versionId, null, 9999, null));
    }

    private const long OversizedForDiff = 10 * 1024 * 1024 + 1;

    [Fact]
    public void OpenVersionsForDiff_with_only_the_left_side_oversized_throws_DiffContentTooLarge()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, OversizedForDiff, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 11, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new FileVersionService(repository, contentStore);

        var ex = Assert.IsType<OversizedDiffRefusal>(
            GetDiffRefusal(service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null)));

        Assert.Equal(OversizedForDiff, ex.LeftSize);
        Assert.Null(ex.RightSize);
    }

    [Fact]
    public void OpenVersionsForDiff_with_only_the_right_side_oversized_throws_DiffContentTooLarge()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, 11, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, OversizedForDiff, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new FileVersionService(repository, contentStore);

        var ex = Assert.IsType<OversizedDiffRefusal>(
            GetDiffRefusal(service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null)));

        Assert.Null(ex.LeftSize);
        Assert.Equal(OversizedForDiff, ex.RightSize);
    }

    [Fact]
    public void OpenVersionsForDiff_with_both_sides_oversized_throws_DiffContentTooLarge_naming_both()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, OversizedForDiff, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, OversizedForDiff + 1, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new FileVersionService(repository, contentStore);

        var ex = Assert.IsType<OversizedDiffRefusal>(
            GetDiffRefusal(service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null)));

        Assert.Equal(OversizedForDiff, ex.LeftSize);
        Assert.Equal(OversizedForDiff + 1, ex.RightSize);
    }

    private static byte[] BinaryContent(int nulOffset, int totalLength)
    {
        var bytes = new byte[totalLength];
        Array.Fill(bytes, (byte)'x');
        bytes[nulOffset] = 0;
        return bytes;
    }

    [Fact]
    public void OpenVersionsForDiff_with_only_the_left_side_binary_throws_DiffBinaryContent()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream(BinaryContent(nulOffset: 5, totalLength: 20)));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, 20, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 11, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new FileVersionService(repository, contentStore);

        var ex = Assert.IsType<BinaryDiffRefusal>(
            GetDiffRefusal(service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null)));

        Assert.True(ex.LeftIsBinary);
        Assert.False(ex.RightIsBinary);
    }

    [Fact]
    public void OpenVersionsForDiff_with_only_the_right_side_binary_throws_DiffBinaryContent()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream(BinaryContent(nulOffset: 5, totalLength: 20)));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, 11, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 20, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new FileVersionService(repository, contentStore);

        var ex = Assert.IsType<BinaryDiffRefusal>(
            GetDiffRefusal(service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null)));

        Assert.False(ex.LeftIsBinary);
        Assert.True(ex.RightIsBinary);
    }

    [Fact]
    public void OpenVersionsForDiff_with_both_sides_binary_throws_DiffBinaryContent_naming_both()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream(BinaryContent(nulOffset: 3, totalLength: 20)));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream(BinaryContent(nulOffset: 7, totalLength: 20)));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, 20, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 20, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new FileVersionService(repository, contentStore);

        var ex = Assert.IsType<BinaryDiffRefusal>(
            GetDiffRefusal(service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null)));

        Assert.True(ex.LeftIsBinary);
        Assert.True(ex.RightIsBinary);
    }

    [Fact]
    public void OpenVersionsForDiff_with_a_NUL_byte_beyond_the_binary_sample_still_diffs_and_rewinds_the_stream()
    {
        // A NUL byte placed after the first 8000 bytes must not trip the binary check, and the
        // sample read used to check for it must not leave the returned stream partially consumed.
        var content = new byte[9000];
        Array.Fill(content, (byte)'x');
        content[8500] = 0;
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream(content));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, content.Length, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 11, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new FileVersionService(repository, contentStore);

        using var streams = GetDiffStreams(service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null));
        var (left, right) = (streams.Left, streams.Right);
        using var leftStream = left;
        using var rightStream = right;
        using var leftBuffer = new MemoryStream();
        leftStream.CopyTo(leftBuffer);

        Assert.Equal(content, leftBuffer.ToArray());
    }

    [Fact]
    public void OpenVersionsForDiff_with_one_side_oversized_and_the_other_binary_reports_only_the_size_error()
    {
        // Size is checked before binary sampling, so a comparison already refused on size never
        // even samples the other side for binary content.
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream(BinaryContent(nulOffset: 5, totalLength: 20)));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, OversizedForDiff, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 20, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new FileVersionService(repository, contentStore);

        var ex = Assert.IsType<OversizedDiffRefusal>(
            GetDiffRefusal(service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null)));

        Assert.Equal(OversizedForDiff, ex.LeftSize);
        Assert.Null(ex.RightSize);
    }

    [Fact]
    public void ShowVersion_of_a_linked_entry_throws_ShowOrDiffLinkedEntry_instead_of_MissingContentHash()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "link", null, null, 0, t0, FileChangeKind.Linked, t0, linkTarget: @"C:\target");
        var versionId = repository.GetFileHistory("link").Single().Id;
        var service = new FileVersionService(repository, new FakeContentStore());

        var ex = Assert.IsType<LinkedEntryShowRefusal>(GetShowRefusal(service.ShowVersion("link", versionId, asOf: null)));

        Assert.Equal("link", ex.RelativePath);
    }

    [Fact]
    public void OpenVersionsForDiff_with_only_the_left_side_linked_throws_ShowOrDiffLinkedEntry_naming_the_left_side()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "link", null, null, 0, t0, FileChangeKind.Linked, t0, linkTarget: @"C:\target");
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "link", null, hash, 11, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("link").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new FileVersionService(repository, contentStore);

        var ex = Assert.IsType<LinkedEntryDiffRefusal>(
            GetDiffRefusal(service.OpenVersionsForDiff("link", versionIds[0], null, versionIds[1], null)));

        Assert.True(ex.LeftIsLinked);
        Assert.False(ex.RightIsLinked);
    }

    [Fact]
    public void OpenVersionsForDiff_with_only_the_right_side_linked_throws_ShowOrDiffLinkedEntry_naming_the_right_side()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "link", null, hash, 11, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "link", null, null, 0, t1, FileChangeKind.Linked, t1, linkTarget: @"C:\target");
        var versionIds = repository.GetFileHistory("link").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new FileVersionService(repository, contentStore);

        var ex = Assert.IsType<LinkedEntryDiffRefusal>(
            GetDiffRefusal(service.OpenVersionsForDiff("link", versionIds[0], null, versionIds[1], null)));

        Assert.False(ex.LeftIsLinked);
        Assert.True(ex.RightIsLinked);
    }

    [Fact]
    public void OpenVersionsForDiff_disposes_the_already_opened_left_stream_when_the_right_side_fails_to_open()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        contentStore.ThrowOnOpenReadForHash = hashV2;
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, 11, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 11, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new FileVersionService(repository, contentStore);

        Assert.Throws<FileNotFoundException>(() => service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null));

        Assert.Contains(hashV1, contentStore.DisposedHashes);
    }
}
