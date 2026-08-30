using Vara.Application.Backup;
using Vara.Core.Abstractions;
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

    private static string HashOf(string content) => new FakeHasher().ComputeHash(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));

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
}
