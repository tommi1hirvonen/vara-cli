using System.Runtime.InteropServices;

namespace Vara.Infrastructure.Interop;

/// <summary>
/// Win32 hard-link creation via <c>CreateHardLinkW</c>, P/Invoked directly because
/// <c>System.IO.File.CreateHardLink</c> is not available on the <c>net10.0</c> target
/// framework (it ships starting with .NET 11). Uses source-generated
/// (<see cref="LibraryImportAttribute"/>) marshalling, which is fully Native AOT /
/// trimming safe - no runtime marshalling stub generation or reflection involved.
/// </summary>
internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes);

    /// <summary>
    /// Creates a new hard link at <paramref name="newLinkPath"/> pointing at the same
    /// file content as <paramref name="existingFilePath"/>.
    /// </summary>
    /// <exception cref="IOException">
    /// The link could not be created (e.g. the volume does not support hard links,
    /// or the paths are on different volumes).
    /// </exception>
    public static void CreateHardLink(string newLinkPath, string existingFilePath)
    {
        if (!CreateHardLinkW(newLinkPath, existingFilePath, 0))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Failed to create hard link '{newLinkPath}' -> '{existingFilePath}' (Win32 error {error}).",
                error);
        }
    }
}
