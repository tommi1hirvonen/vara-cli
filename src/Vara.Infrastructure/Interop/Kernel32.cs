using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vara.Infrastructure.Interop;

/// <summary>
/// Win32 hard-link creation via <c>CreateHardLinkW</c>, P/Invoked directly because
/// <c>System.IO.File.CreateHardLink</c> is not available on the <c>net10.0</c> target
/// framework (it ships starting with .NET 11); and real-path resolution via
/// <c>CreateFileW</c>/<c>GetFinalPathNameByHandleW</c> (see <see cref="ResolveRealPath"/>),
/// needed because <see cref="FileSystemInfo.ResolveLinkTarget"/> only resolves a symlink at
/// the path itself, not a reparse point elsewhere in its ancestry. Uses source-generated
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

    private const uint GenericRead = 0x8000_0000;
    private const uint FileShareReadWriteDelete = 0x0000_0007; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x0200_0000; // required to open a directory handle
    private const uint FileNameNormalized = 0x0;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle hFile, char* lpszFilePath, uint cchFilePath, uint dwFlags);

    /// <summary>
    /// Resolves <paramref name="path"/> - an existing file or directory - to its real, final
    /// location via <c>GetFinalPathNameByHandle</c>, following any reparse points along the
    /// way. Opens <paramref name="path"/> with <c>FILE_FLAG_BACKUP_SEMANTICS</c> (required to
    /// open a directory handle) before resolving.
    /// </summary>
    /// <exception cref="IOException">The path could not be opened or resolved.</exception>
    public static unsafe string GetFinalPathName(string path)
    {
        using var handle = CreateFileW(
            path,
            GenericRead,
            FileShareReadWriteDelete,
            lpSecurityAttributes: 0,
            OpenExisting,
            FileFlagBackupSemantics,
            hTemplateFile: 0);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException($"Failed to open '{path}' to resolve its real path (Win32 error {error}).", error);
        }

        // A first call with a zero-length buffer returns the required buffer size (including
        // the null terminator) instead of failing, per GetFinalPathNameByHandle's documented
        // contract.
        var requiredSize = GetFinalPathNameByHandleW(handle, null, 0, FileNameNormalized);
        if (requiredSize == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException($"Failed to resolve the real path of '{path}' (Win32 error {error}).", error);
        }

        var buffer = new char[requiredSize];
        fixed (char* bufferPtr = buffer)
        {
            var writtenSize = GetFinalPathNameByHandleW(handle, bufferPtr, requiredSize, FileNameNormalized);
            if (writtenSize == 0 || writtenSize >= requiredSize)
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"Failed to resolve the real path of '{path}' (Win32 error {error}).", error);
            }

            return new string(buffer, 0, (int)writtenSize);
        }
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to its real, final location on disk, following any
    /// reparse points (symlinks, junctions, mount points) anywhere along its ancestor chain -
    /// unlike <see cref="FileSystemInfo.ResolveLinkTarget"/>, which only resolves a symlink at
    /// the path itself and returns <see langword="null"/> for an ordinary file or directory
    /// sitting past a reparse point higher up its ancestry (the case this exists to cover).
    /// Walks up to the deepest ancestor of <paramref name="path"/> that currently exists (a
    /// restore destination commonly does not exist yet), resolves that ancestor's real path via
    /// <see cref="GetFinalPathName"/>, then re-appends any not-yet-existing trailing segments
    /// literally. Falls back to the plain, lexical <see cref="Path.GetFullPath(string)"/> result
    /// if no ancestor at all can be opened (for example, every ancestor up to a drive root is
    /// inaccessible under restrictive ACLs) - a fail-safe that preserves at least today's level
    /// of protection rather than throwing out of a routine containment check.
    /// </summary>
    public static string ResolveRealPath(string path)
    {
        var absolute = Path.GetFullPath(path);

        var trailingSegments = new List<string>();
        var ancestor = absolute;
        while (!Directory.Exists(ancestor) && !File.Exists(ancestor))
        {
            var parent = Path.GetDirectoryName(ancestor);
            if (string.IsNullOrEmpty(parent) || parent == ancestor)
            {
                // Reached a drive root (or an unparsable path) without finding an existing
                // ancestor to open a handle to.
                return absolute;
            }

            trailingSegments.Add(Path.GetFileName(ancestor));
            ancestor = parent;
        }

        string realAncestor;
        try
        {
            realAncestor = GetFinalPathName(ancestor);
        }
        catch (IOException)
        {
            return absolute;
        }

        // GetFinalPathNameByHandle returns an extended-length ("\\?\", or "\\?\UNC\" for a UNC
        // path) form; strip that prefix so the result compares consistently with ordinary
        // Path.GetFullPath output used elsewhere in this codebase.
        if (realAncestor.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            realAncestor = @"\\" + realAncestor[8..];
        }
        else if (realAncestor.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            realAncestor = realAncestor[4..];
        }

        if (trailingSegments.Count == 0)
        {
            return realAncestor;
        }

        trailingSegments.Reverse();
        return Path.Combine([realAncestor, .. trailingSegments]);
    }
}
