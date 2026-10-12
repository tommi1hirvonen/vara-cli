using Vara.Core.Abstractions;
using Xunit;

namespace Vara.Core.Tests.Abstractions;

public class ScanItemTests
{
    [Fact]
    public void File_and_link_cases_are_retrieved_without_allocations()
    {
        var fileItem = ScanItem.File("file.txt", @"C:\src\file.txt", 17, DateTimeOffset.UnixEpoch);
        var linkItem = ScanItem.Link("link", @"C:\src\link", @"C:\target");
        var emptyItem = default(ScanItem);

        Assert.True(fileItem.HasValue);
        Assert.True(linkItem.HasValue);
        Assert.False(emptyItem.HasValue);
        Assert.True(fileItem.TryGetValue(out ScannedFile file));
        Assert.Equal("file.txt", file.RelativePath);
        Assert.Equal(17, file.Size);
        Assert.True(linkItem.TryGetValue(out ScannedLink link));
        Assert.Equal("link", link.RelativePath);
        Assert.Equal(@"C:\target", link.LinkTarget);

        for (var i = 0; i < 10; i++)
        {
            _ = fileItem.TryGetValue(out ScannedFile _);
            _ = linkItem.TryGetValue(out ScannedLink _);
        }

        long observedValues = 0;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            if (fileItem.TryGetValue(out ScannedFile scannedFile))
            {
                observedValues += scannedFile.Size;
            }

            if (linkItem.TryGetValue(out ScannedLink scannedLink))
            {
                observedValues += scannedLink.RelativePath.Length;
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(21_000, observedValues);
        Assert.Equal(0, allocated);
    }
}
