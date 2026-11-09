module Vara.Engine.Tests.InteropTests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open Xunit
open Vara.Engine
open Vara.Engine.Tests

[<DllImport("kernel32.dll", CharSet = CharSet.Unicode)>]
extern uint32 private GetShortPathNameW(string longPath, StringBuilder shortPath, uint32 length)

let createJunction (junction: string) (target: string) =
    let info = ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.UseShellExecute <- false
    use proc = Process.Start info |> nonNull
    proc.WaitForExit()
    Assert.Equal(0, proc.ExitCode)

let private sameLocation (expected: string) (actual: string) =
    Assert.Equal(Path.GetFullPath expected, actual, true)

module ``Hard links`` =
    [<Fact>]
    let ``A hard link shares the content of the existing file`` () =
        TempDir.using (fun root ->
            let existing = Path.Combine(root, "a.txt")
            let link = Path.Combine(root, "b.txt")
            TempDir.write existing "content"
            Kernel32.createHardLink link existing
            Assert.Equal("content", File.ReadAllText link)
            // Writing through one name is visible through the other: it is the same file.
            File.AppendAllText(existing, "!")
            Assert.Equal("content!", File.ReadAllText link))

    [<Fact>]
    let ``A failing link raises an IOException that carries the Win32 error`` () =
        TempDir.using (fun root ->
            let ex =
                Assert.Throws<IOException>(fun () ->
                    Kernel32.createHardLink (Path.Combine(root, "b.txt")) (Path.Combine(root, "missing.txt")))

            // ERROR_FILE_NOT_FOUND
            Assert.Equal(2, ex.HResult &&& 0xFFFF)
            Assert.Contains("Win32 error 2", ex.Message))

module ``Real path resolution`` =
    [<Fact>]
    let ``A plain path resolves unchanged`` () =
        TempDir.using (fun root ->
            let file = Path.Combine(root, "file.txt")
            TempDir.write file "x"
            sameLocation file (Kernel32.resolveRealPath file))

    [<Fact>]
    let ``A path through a junction resolves to the junction's target`` () =
        TempDir.using (fun root ->
            let outside = Path.Combine(root, "outside")
            TempDir.write (Path.Combine(outside, "file.txt")) "x"
            let junction = Path.Combine(root, "linked")
            createJunction junction outside
            sameLocation (Path.Combine(outside, "file.txt")) (Kernel32.resolveRealPath (Path.Combine(junction, "file.txt"))))

    [<Fact>]
    let ``A destination that does not exist keeps its trailing segments`` () =
        TempDir.using (fun root ->
            let destination = Path.Combine(root, "new", "deeper", "file.txt")
            sameLocation destination (Kernel32.resolveRealPath destination))

    [<Fact>]
    let ``A destination that does not exist resolves through a junction`` () =
        TempDir.using (fun root ->
            let outside = Path.Combine(root, "outside")
            Directory.CreateDirectory outside |> ignore
            let junction = Path.Combine(root, "linked")
            createJunction junction outside

            sameLocation
                (Path.Combine(outside, "sub", "new.txt"))
                (Kernel32.resolveRealPath (Path.Combine(junction, "sub", "new.txt"))))

    [<Fact>]
    let ``A short path resolves to the long path`` () =
        TempDir.using (fun root ->
            let longDirectory = Path.Combine(root, "A directory with a long name")
            TempDir.write (Path.Combine(longDirectory, "A file with a long name.txt")) "x"
            let shortBuffer = StringBuilder 260
            let written = GetShortPathNameW(longDirectory, shortBuffer, 260u)

            if written > 0u && not (String.Equals(shortBuffer.ToString(), longDirectory, StringComparison.OrdinalIgnoreCase)) then
                let shortPath = Path.Combine(shortBuffer.ToString(), "A file with a long name.txt")
                Assert.DoesNotContain("A directory with a long name", shortPath)
                // The resolved form may differ from the temp path in the root's own short form, so compare the tail.
                Assert.EndsWith(@"A directory with a long name\A file with a long name.txt", Kernel32.resolveRealPath shortPath, StringComparison.OrdinalIgnoreCase)
            else
                // 8.3 names are disabled on this volume, so there is no short form to resolve.
                ())

    [<Fact>]
    let ``A path longer than the stack buffer resolves`` () =
        TempDir.using (fun root ->
            let deep = Path.Combine(root, String('a', 200), String('b', 200), String('c', 200))
            Directory.CreateDirectory deep |> ignore
            let file = Path.Combine(deep, "file.txt")
            File.WriteAllText(file, "x")
            Assert.True(file.Length > 600)
            sameLocation file (Kernel32.resolveRealPath file))

module Streams =
    let private bytes (length: int) =
        let data = Array.zeroCreate<byte> length
        Random.Shared.NextBytes data
        data

    let private readAll (stream: Stream) (chunk: int) : byte[] =
        use result = new MemoryStream()
        let buffer = Array.zeroCreate<byte> chunk
        let mutable read = stream.Read(buffer, 0, chunk)

        while read > 0 do
            result.Write(buffer, 0, read)
            read <- stream.Read(buffer, 0, chunk)

        result.ToArray()

    [<Fact>]
    let ``Replay yields the prefix and then the rest, byte for byte`` () =
        let prefix = bytes 500
        let rest = bytes 1500
        use inner = new MemoryStream(rest)
        use replay = new PrefixReplayStream(prefix, inner)
        Assert.Equal<byte[]>(Array.append prefix rest, readAll replay 64)

    [<Fact>]
    let ``Replay through a span read matches`` () =
        let prefix = bytes 300
        let rest = bytes 700
        use inner = new MemoryStream(rest)
        use replay = new PrefixReplayStream(prefix, inner)
        use result = new MemoryStream()
        let buffer = Array.zeroCreate<byte> 128
        let mutable read = replay.Read(Span<byte> buffer)

        while read > 0 do
            result.Write(buffer, 0, read)
            read <- replay.Read(Span<byte> buffer)

        Assert.Equal<byte[]>(Array.append prefix rest, result.ToArray())

    [<Fact>]
    let ``Replay refuses to seek`` () =
        use replay = new PrefixReplayStream([||], new MemoryStream())
        Assert.False replay.CanSeek
        Assert.Throws<NotSupportedException>(fun () -> replay.Seek(0L, SeekOrigin.Begin) |> ignore) |> ignore

    [<Fact>]
    let ``Tee copies every byte read into the sink and reports each chunk`` () =
        let data = bytes 5000
        use source = new MemoryStream(data)
        use sink = new MemoryStream()
        let reported = ref 0L
        use tee = new TeeStream(source, sink, ValueSome(fun count -> reported.Value <- reported.Value + count))
        Assert.Equal<byte[]>(data, readAll tee 777)
        Assert.Equal<byte[]>(data, sink.ToArray())
        Assert.Equal(5000L, tee.TotalBytesCopied)
        Assert.Equal(5000L, reported.Value)
