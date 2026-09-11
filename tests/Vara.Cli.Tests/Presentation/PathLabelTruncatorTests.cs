using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class PathLabelTruncatorTests
{
    [Fact]
    public void Path_within_budget_is_returned_unchanged()
    {
        var result = PathLabelTruncator.Truncate(@"C:\a\b\file.txt", 30);

        Assert.Equal(@"C:\a\b\file.txt", result);
    }

    [Fact]
    public void Deeply_nested_path_is_truncated_to_trailing_segments_prefixed_with_ellipsis()
    {
        var result = PathLabelTruncator.Truncate(@"C:\a\b\c\d\e\file.txt", 15);

        Assert.True(result.Length <= 15);
        Assert.StartsWith("...", result);
        Assert.EndsWith(@"file.txt", result);
    }

    [Fact]
    public void Result_never_exceeds_the_requested_budget_for_a_deeply_nested_path()
    {
        var result = PathLabelTruncator.Truncate(@"C:\alpha\bravo\charlie\delta\echo\foxtrot\file.txt", 20);

        Assert.True(result.Length <= 20);
        Assert.EndsWith("file.txt", result);
    }

    [Fact]
    public void Filename_alone_longer_than_budget_is_truncated_with_a_leading_ellipsis()
    {
        var result = PathLabelTruncator.Truncate(@"C:\a\aVeryLongFileNameThatIsTooLongToFitInTheBudget.txt", 15);

        Assert.Equal(15, result.Length);
        Assert.StartsWith("...", result);

        // The kept characters are the filename's own trailing characters, not a parent segment.
        Assert.EndsWith("Budget.txt", result);
    }

    [Fact]
    public void Zero_or_negative_budget_returns_an_empty_string()
    {
        Assert.Equal(string.Empty, PathLabelTruncator.Truncate(@"C:\a\file.txt", 0));
        Assert.Equal(string.Empty, PathLabelTruncator.Truncate(@"C:\a\file.txt", -1));
    }

    [Fact]
    public void Budget_too_tight_for_the_ellipsis_prefix_returns_the_filename_alone_within_budget()
    {
        // Regression test: the filename ("file.txt", 8 chars) exactly fills a budget of 8, but
        // an earlier version of the algorithm still unconditionally prepended "...\" (4 more
        // characters) in front of it, overflowing the requested budget. No parent segment can
        // be added here either way, so the correct result is the bare filename, not an
        // over-length "...\file.txt".
        var result = PathLabelTruncator.Truncate(@"C:\a-long-parent-directory\file.txt", 8);

        Assert.Equal("file.txt", result);
    }
}
