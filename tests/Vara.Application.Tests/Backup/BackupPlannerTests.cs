using Vara.Application.Backup;
using Vara.Core.Abstractions;
using Vara.Core.Hashing;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Application.Tests.Backup;

public class BackupPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vara-test-{Guid.NewGuid():N}");
    private readonly BackupPlanner _planner = new(new FakeHasher());

    public BackupPlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string HashOf(string content) => new FakeHasher().ComputeHash(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));

    private static string HashOf(byte[] content) => new FakeHasher().ComputeHash(new MemoryStream(content));

    private static string QuickHashOf(byte[] content) =>
        HashOf(content[..Math.Min(content.Length, QuickHashPolicy.WindowSizeBytes)]);

    private static byte[] RepeatingBytes(int length, byte fill)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }

    [Fact]
    public void A_genuinely_new_file_is_planned_as_an_add_and_counted_in_the_byte_total()
    {
        var path = WriteFile("new.txt", "hello world");
        var entry = new ScannedEntry("new.txt", path, 11, DateTimeOffset.UtcNow, false, null);
        var diff = new DiffResult([new PendingChange(entry, PendingChangeKind.Added)], []);

        var plan = _planner.Plan(diff, new Dictionary<string, CurrentFileState>());

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(PlannedOperationKind.Add, operation.Kind);
        Assert.Equal(11, plan.TotalBytesToTransfer);
    }

    [Fact]
    public void A_linked_entry_produces_a_metadata_only_link_operation_carrying_the_target_path()
    {
        var entry = new ScannedEntry("link", @"C:\src\link", 0, DateTimeOffset.MinValue, true, @"C:\target");
        var diff = new DiffResult([new PendingChange(entry, PendingChangeKind.Linked)], []);

        var plan = _planner.Plan(diff, new Dictionary<string, CurrentFileState>());

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(PlannedOperationKind.Link, operation.Kind);
        Assert.Equal("link", operation.RelativePath);
        Assert.Null(operation.KnownContentHash);
        Assert.Equal(@"C:\target", operation.LinkTarget);
        Assert.Equal(0, plan.TotalBytesToTransfer);
    }

    [Fact]
    public void A_moved_file_is_detected_via_matching_hash_and_excluded_from_the_byte_total()
    {
        var content = "identical content, relocated";
        var newPath = WriteFile("Documents/report.pdf".Replace('/', Path.DirectorySeparatorChar), content);
        var size = new FileInfo(newPath).Length;

        var scannedEntry = new ScannedEntry(@"Documents\report.pdf", newPath, size, DateTimeOffset.UtcNow, false, null);
        var diff = new DiffResult([new PendingChange(scannedEntry, PendingChangeKind.Added)], [@"Downloads\report.pdf"]);
        var currentState = new Dictionary<string, CurrentFileState>
        {
            [@"Downloads\report.pdf"] = new(@"Downloads\report.pdf", HashOf(content), size, DateTimeOffset.UtcNow.AddDays(-1)),
        };

        var plan = _planner.Plan(diff, currentState);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(PlannedOperationKind.Move, operation.Kind);
        Assert.Equal(@"Downloads\report.pdf", operation.PreviousRelativePath);
        Assert.Equal(0, plan.TotalBytesToTransfer);
    }

    [Fact]
    public void A_same_size_but_different_content_file_is_not_mistaken_for_a_move()
    {
        var newPath = WriteFile("new.txt", "AAAAAAAAAA");
        var size = new FileInfo(newPath).Length;

        var scannedEntry = new ScannedEntry("new.txt", newPath, size, DateTimeOffset.UtcNow, false, null);
        var diff = new DiffResult([new PendingChange(scannedEntry, PendingChangeKind.Added)], ["old.txt"]);
        var currentState = new Dictionary<string, CurrentFileState>
        {
            ["old.txt"] = new("old.txt", HashOf("BBBBBBBBBB"), size, DateTimeOffset.UtcNow.AddDays(-1)),
        };

        var plan = _planner.Plan(diff, currentState);

        Assert.Equal(2, plan.Operations.Count); // an Add and a Delete, not a Move
        Assert.Contains(plan.Operations, o => o.Kind == PlannedOperationKind.Add);
        Assert.Contains(plan.Operations, o => o.Kind == PlannedOperationKind.Delete);
        Assert.Equal(size, plan.TotalBytesToTransfer);
    }

    [Fact]
    public void A_changed_entry_is_never_treated_as_a_move_candidate()
    {
        var path = WriteFile("existing.txt", "new content, same size as deleted file");
        var size = new FileInfo(path).Length;

        var scannedEntry = new ScannedEntry("existing.txt", path, size, DateTimeOffset.UtcNow, false, null);
        var diff = new DiffResult([new PendingChange(scannedEntry, PendingChangeKind.Changed)], ["deleted.txt"]);
        var currentState = new Dictionary<string, CurrentFileState>
        {
            ["existing.txt"] = new("existing.txt", HashOf("old content"), size, DateTimeOffset.UtcNow.AddDays(-1)),
            ["deleted.txt"] = new("deleted.txt", HashOf("new content, same size as deleted file"), size, DateTimeOffset.UtcNow.AddDays(-1)),
        };

        var plan = _planner.Plan(diff, currentState);

        Assert.Contains(plan.Operations, o => o.Kind == PlannedOperationKind.Change && o.RelativePath == "existing.txt");
        Assert.Contains(plan.Operations, o => o.Kind == PlannedOperationKind.Delete && o.RelativePath == "deleted.txt");
    }

    [Fact]
    public void Deletions_not_consumed_by_a_move_are_planned_as_deletes()
    {
        var now = DateTimeOffset.UtcNow;
        var diff = new DiffResult([], ["gone.txt"]);
        var currentState = new Dictionary<string, CurrentFileState> { ["gone.txt"] = new("gone.txt", "hash", 5, now) };

        var plan = _planner.Plan(diff, currentState);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(PlannedOperationKind.Delete, operation.Kind);
        Assert.Equal(0, plan.TotalBytesToTransfer);
    }

    [Fact]
    public void A_same_size_candidate_ruled_out_by_quick_hash_is_never_fully_read()
    {
        // Larger than the quick-hash window so a full read would hash strictly more
        // bytes than the bounded prefix - proves the rest of the file was never read.
        var addedContent = RepeatingBytes(QuickHashPolicy.WindowSizeBytes * 2, fill: 0xAA);
        var deletedContent = RepeatingBytes(QuickHashPolicy.WindowSizeBytes * 2, fill: 0xBB);
        var path = WriteFile("new.bin", addedContent);
        var hasher = new CountingHasher();
        var planner = new BackupPlanner(hasher);

        var scannedEntry = new ScannedEntry("new.bin", path, addedContent.Length, DateTimeOffset.UtcNow, false, null);
        var diff = new DiffResult([new PendingChange(scannedEntry, PendingChangeKind.Added)], ["old.bin"]);
        var currentState = new Dictionary<string, CurrentFileState>
        {
            ["old.bin"] = new(
                "old.bin", HashOf(deletedContent), deletedContent.Length, DateTimeOffset.UtcNow.AddDays(-1),
                QuickHashOf(deletedContent), QuickHashPolicy.CurrentScheme),
        };

        var plan = planner.Plan(diff, currentState);

        Assert.Contains(plan.Operations, o => o.Kind == PlannedOperationKind.Add);
        var bytesHashed = Assert.Single(hasher.CallLengths);
        Assert.Equal(QuickHashPolicy.WindowSizeBytes, bytesHashed); // only the bounded prefix was ever hashed/read
    }

    [Fact]
    public void A_same_size_candidate_matched_by_quick_hash_is_confirmed_via_a_full_read()
    {
        var content = RepeatingBytes(QuickHashPolicy.WindowSizeBytes * 2, fill: 0xCC);
        var path = WriteFile("Documents/report.bin".Replace('/', Path.DirectorySeparatorChar), content);
        var hasher = new CountingHasher();
        var planner = new BackupPlanner(hasher);

        var scannedEntry = new ScannedEntry(@"Documents\report.bin", path, content.Length, DateTimeOffset.UtcNow, false, null);
        var diff = new DiffResult([new PendingChange(scannedEntry, PendingChangeKind.Added)], [@"Downloads\report.bin"]);
        var currentState = new Dictionary<string, CurrentFileState>
        {
            [@"Downloads\report.bin"] = new(
                @"Downloads\report.bin", HashOf(content), content.Length, DateTimeOffset.UtcNow.AddDays(-1),
                QuickHashOf(content), QuickHashPolicy.CurrentScheme),
        };

        var plan = planner.Plan(diff, currentState);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(PlannedOperationKind.Move, operation.Kind);
        Assert.Equal(2, hasher.CallLengths.Count); // the quick hash, then a full-content confirmation
        Assert.Contains(content.Length, hasher.CallLengths);
    }

    [Fact]
    public void A_candidate_missing_a_recorded_quick_hash_still_gets_a_full_read_and_correct_match()
    {
        // "unmatched" has a recorded (but non-matching) quick hash and can be cheaply
        // ruled out; "no-signature" has none recorded at all, so it cannot be ruled out
        // cheaply and forces a full read - which is exactly what correctly finds it as
        // the true match, proving the fallback doesn't silently misclassify the file.
        var content = RepeatingBytes(50, fill: 0x11);
        var differentSameSizeContent = RepeatingBytes(50, fill: 0x22);
        var path = WriteFile("new.bin", content);

        var scannedEntry = new ScannedEntry("new.bin", path, content.Length, DateTimeOffset.UtcNow, false, null);
        var diff = new DiffResult([new PendingChange(scannedEntry, PendingChangeKind.Added)], ["unmatched.bin", "no-signature.bin"]);
        var currentState = new Dictionary<string, CurrentFileState>
        {
            ["unmatched.bin"] = new(
                "unmatched.bin", HashOf(differentSameSizeContent), differentSameSizeContent.Length, DateTimeOffset.UtcNow.AddDays(-1),
                QuickHashOf(differentSameSizeContent), QuickHashPolicy.CurrentScheme),
            ["no-signature.bin"] = new("no-signature.bin", HashOf(content), content.Length, DateTimeOffset.UtcNow.AddDays(-1)),
        };

        var plan = _planner.Plan(diff, currentState);

        Assert.Contains(plan.Operations, o => o.Kind == PlannedOperationKind.Move && o.PreviousRelativePath == "no-signature.bin");
        Assert.Contains(plan.Operations, o => o.Kind == PlannedOperationKind.Delete && o.RelativePath == "unmatched.bin");
    }

    [Fact]
    public void A_tie_between_identical_candidates_is_broken_by_deleted_path_order_under_parallel_hashing()
    {
        var content = "identical content, relocated";
        var path = WriteFile("new.txt", content);
        var size = new FileInfo(path).Length;

        // Many size-matched entries force the parallel signature-computation phase to
        // actually run across multiple candidates concurrently, not just a single one.
        var otherEntries = Enumerable.Range(0, 20)
            .Select(i => new PendingChange(
                new ScannedEntry($"other-{i}.txt", WriteFile($"other-{i}.txt", $"unrelated content {i}"), size, DateTimeOffset.UtcNow, false, null),
                PendingChangeKind.Added))
            .ToList();
        var scannedEntry = new ScannedEntry("new.txt", path, size, DateTimeOffset.UtcNow, false, null);
        var pending = new List<PendingChange> { new(scannedEntry, PendingChangeKind.Added) };
        pending.AddRange(otherEntries);

        var diff = new DiffResult(pending, ["first-candidate.txt", "second-candidate.txt"]);
        var currentState = new Dictionary<string, CurrentFileState>
        {
            ["first-candidate.txt"] = new("first-candidate.txt", HashOf(content), size, DateTimeOffset.UtcNow.AddDays(-1)),
            ["second-candidate.txt"] = new("second-candidate.txt", HashOf(content), size, DateTimeOffset.UtcNow.AddDays(-1)),
        };

        var plan = _planner.Plan(diff, currentState);

        var move = Assert.Single(plan.Operations, o => o.Kind == PlannedOperationKind.Move);
        Assert.Equal("first-candidate.txt", move.PreviousRelativePath); // first in DeletedPaths order wins the tie
        Assert.Contains(plan.Operations, o => o.Kind == PlannedOperationKind.Delete && o.RelativePath == "second-candidate.txt");
    }

    [Fact]
    public void Unconfigured_scan_concurrency_permits_more_than_one_concurrent_hash_call()
    {
        // Documents that this change intentionally leaves BackupPlanner's own default
        // (Environment.ProcessorCount) untouched, unlike BackupExecutor's new default
        // of 1 (configure-backup-concurrency) - this stage only ever reads the source.
        const int candidateCount = 20;
        var deletedContent = RepeatingBytes(64, fill: 0xFF);
        var deletedPaths = new List<string>();
        var currentState = new Dictionary<string, CurrentFileState>();
        for (var i = 0; i < candidateCount; i++)
        {
            var name = $"deleted-{i}.bin";
            deletedPaths.Add(name);
            currentState[name] = new(name, HashOf(deletedContent), deletedContent.Length, DateTimeOffset.UtcNow.AddDays(-1));
        }

        var pending = Enumerable.Range(0, candidateCount)
            .Select(i =>
            {
                var content = RepeatingBytes(64, fill: (byte)i);
                var path = WriteFile($"added-{i}.bin", content);
                return new PendingChange(
                    new ScannedEntry($"added-{i}.bin", path, content.Length, DateTimeOffset.UtcNow, false, null), PendingChangeKind.Added);
            })
            .ToList();

        var diff = new DiffResult(pending, deletedPaths);
        var hasher = new ConcurrencyObservingHasher();
        var planner = new BackupPlanner(hasher);

        planner.Plan(diff, currentState);

        Assert.True(hasher.MaxObservedConcurrency > 1, $"expected more than one concurrent hash call by default, observed {hasher.MaxObservedConcurrency}");
    }

    [Fact]
    public void A_changed_entrys_previous_content_hash_is_carried_from_current_state()
    {
        // PlaceAtMirrorPath needs the previous content's hash to restore read-only
        // protection on that content's blob after overwriting the mirror entry
        // (protect-hardlinked-mirror-files change's design.md) - already available in
        // currentState at plan time, distinct from KnownContentHash (the new content's
        // hash, unresolved until execution for Add/Change).
        var path = WriteFile("existing.txt", "new content");
        var size = new FileInfo(path).Length;
        var previousHash = HashOf("old content");

        var scannedEntry = new ScannedEntry("existing.txt", path, size, DateTimeOffset.UtcNow, false, null);
        var diff = new DiffResult([new PendingChange(scannedEntry, PendingChangeKind.Changed)], []);
        var currentState = new Dictionary<string, CurrentFileState>
        {
            ["existing.txt"] = new("existing.txt", previousHash, size, DateTimeOffset.UtcNow.AddDays(-1)),
        };

        var plan = _planner.Plan(diff, currentState);

        var operation = Assert.Single(plan.Operations);
        Assert.Equal(PlannedOperationKind.Change, operation.Kind);
        Assert.Equal(previousHash, operation.PreviousContentHash);
    }

    [Fact]
    public void An_added_entry_has_no_previous_content_hash()
    {
        var path = WriteFile("new.txt", "hello world");
        var entry = new ScannedEntry("new.txt", path, 11, DateTimeOffset.UtcNow, false, null);
        var diff = new DiffResult([new PendingChange(entry, PendingChangeKind.Added)], []);

        var plan = _planner.Plan(diff, new Dictionary<string, CurrentFileState>());

        var operation = Assert.Single(plan.Operations);
        Assert.Null(operation.PreviousContentHash);
    }

    /// <summary>Wraps <see cref="FakeHasher"/>, recording the byte-length hashed on every call - used to prove exactly how much of a stream was actually read.</summary>
    private sealed class CountingHasher : IHasher
    {
        private readonly FakeHasher _inner = new();
        public List<long> CallLengths { get; } = [];

        public string ComputeHash(Stream content)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            CallLengths.Add(buffer.Length);
            return _inner.ComputeHash(new MemoryStream(buffer.ToArray()));
        }
    }
}
