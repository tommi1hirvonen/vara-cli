using Vara.Infrastructure.FileSystem;
using Xunit;

namespace Vara.Infrastructure.Tests.FileSystem;

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
}
