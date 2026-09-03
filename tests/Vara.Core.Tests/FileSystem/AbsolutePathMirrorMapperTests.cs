using Vara.Core.FileSystem;
using Xunit;

namespace Vara.Core.Tests.FileSystem;

public class AbsolutePathMirrorMapperTests
{
    [Fact]
    public void A_root_level_drive_file_has_its_colon_stripped()
    {
        Assert.Equal(@"C\file.txt", AbsolutePathMirrorMapper.ToMirrorPath(@"C:\file.txt"));
    }

    [Fact]
    public void A_deeply_nested_path_has_only_its_colon_stripped()
    {
        Assert.Equal(
            @"C\Users\john\Programming\src\main.py",
            AbsolutePathMirrorMapper.ToMirrorPath(@"C:\Users\john\Programming\src\main.py"));
    }

    [Fact]
    public void A_path_with_mixed_separators_has_only_its_colon_stripped()
    {
        Assert.Equal(
            @"C\Users/john\file.txt",
            AbsolutePathMirrorMapper.ToMirrorPath(@"C:\Users/john\file.txt"));
    }

    [Fact]
    public void FromMirrorPath_reinserts_the_colon_after_a_single_character_leading_segment()
    {
        Assert.Equal(@"C:\Users\john\file.txt", AbsolutePathMirrorMapper.FromMirrorPath(@"C\Users\john\file.txt"));
    }

    [Fact]
    public void FromMirrorPath_reverses_ToMirrorPath_for_a_root_level_file()
    {
        const string original = @"C:\file.txt";
        Assert.Equal(original, AbsolutePathMirrorMapper.FromMirrorPath(AbsolutePathMirrorMapper.ToMirrorPath(original)));
    }

    [Fact]
    public void FromMirrorPath_reverses_ToMirrorPath_for_a_deeply_nested_path()
    {
        const string original = @"C:\Users\john\Programming\src\main.py";
        Assert.Equal(original, AbsolutePathMirrorMapper.FromMirrorPath(AbsolutePathMirrorMapper.ToMirrorPath(original)));
    }

    [Fact]
    public void FromMirrorPath_leaves_a_path_without_a_single_character_leading_segment_unchanged()
    {
        Assert.Equal(@"Users\john\file.txt", AbsolutePathMirrorMapper.FromMirrorPath(@"Users\john\file.txt"));
    }
}
