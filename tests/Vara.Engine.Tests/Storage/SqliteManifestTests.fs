module Vara.Engine.Tests.SqliteManifestTests

open System
open System.IO
open Microsoft.Data.Sqlite
open Xunit
open Vara.Domain
open Vara.Engine
open Vara.Engine.Tests
open Vara.Testing

let private at (minutes: int) = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(float minutes)
let private hash (text: string) = (ContentHash.tryCreate text).Value

let private regular (path: string) (hashText: string) (size: int64) (kind: ChangeKind) (minutes: int) : ManifestRow =
    { RowPath = MirrorPath.ofString path
      PreviousPath = ValueNone
      Content =
        RowContent.Regular
            { Hash = hash hashText
              Size = size
              SourceModifiedAt = at 5
              Quick = ValueSome { QuickHash = "q" + hashText; QuickScheme = 1 } }
      Kind = kind
      RecordedAt = at minutes }

let private linked (path: string) (target: string voption) (kind: ChangeKind) (minutes: int) : ManifestRow =
    { RowPath = MirrorPath.ofString path
      PreviousPath = ValueNone
      Content = RowContent.Linked target
      Kind = kind
      RecordedAt = at minutes }

let private database (root: string) : string = Path.Combine(root, ".vara", "profile.db")

/// Records the rows in one snapshot and commits.
let private write (handle: ManifestHandle) (rows: ManifestRow list) : int64 =
    let snapshot = SqliteManifest.beginSnapshot handle (at 0)
    let mutable batch = SqliteManifest.beginBatch handle

    for row in rows do
        SqliteManifest.record batch snapshot row

    SqliteManifest.finishSnapshot batch snapshot SnapshotStatus.Complete (at 1) (Run.initial (at 0)).Counts
    batch <- SqliteManifest.checkpoint batch
    SqliteManifest.discard batch
    snapshot

/// Every column of every file_versions row, in id order, as text.
let dumpRows (path: string) : string list =
    use connection = new SqliteConnection $"Data Source={path};Pooling=False"
    connection.Open()
    use command = connection.CreateCommand()

    command.CommandText <-
        "SELECT id, snapshot_id, relative_path, ifnull(previous_relative_path,'~'), ifnull(content_hash,'~'), size, source_modified_at, change_kind, recorded_at, ifnull(quick_hash,'~'), ifnull(quick_hash_scheme,'~'), ifnull(link_target,'~') FROM file_versions ORDER BY id"

    use reader = command.ExecuteReader()
    let rows = ResizeArray()

    while reader.Read() do
        rows.Add(String.Join("|", [ for i in 0 .. reader.FieldCount - 1 -> string (reader.GetValue i) ]))

    List.ofSeq rows

let legacySchema =
    """
    CREATE TABLE snapshots (
        id INTEGER PRIMARY KEY AUTOINCREMENT, started_at TEXT NOT NULL, completed_at TEXT NULL, status TEXT NOT NULL,
        bytes_transferred INTEGER NOT NULL DEFAULT 0, files_added INTEGER NOT NULL DEFAULT 0,
        files_changed INTEGER NOT NULL DEFAULT 0, files_moved INTEGER NOT NULL DEFAULT 0,
        files_deleted INTEGER NOT NULL DEFAULT 0, files_failed INTEGER NOT NULL DEFAULT 0);
    CREATE TABLE file_versions (
        id INTEGER PRIMARY KEY AUTOINCREMENT, snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
        relative_path TEXT NOT NULL, previous_relative_path TEXT NULL, content_hash TEXT NULL, size INTEGER NOT NULL,
        source_modified_at TEXT NOT NULL, change_kind TEXT NOT NULL, recorded_at TEXT NOT NULL,
        quick_hash TEXT NULL, quick_hash_scheme INTEGER NULL);
    INSERT INTO snapshots (started_at, status) VALUES ('2026-01-01T00:00:00.0000000+00:00', 'Complete');
    INSERT INTO file_versions (snapshot_id, relative_path, content_hash, size, source_modified_at, change_kind, recorded_at)
        VALUES (1, 'C\Photo.JPG', 'aa11', 10, '2026-01-01T00:00:00.0000000+00:00', 'Added', '2026-01-01T00:00:00.0000000+00:00');
    INSERT INTO file_versions (snapshot_id, relative_path, content_hash, size, source_modified_at, change_kind, recorded_at)
        VALUES (1, 'C\photo.jpg', 'bb22', 11, '2026-01-02T00:00:00.0000000+00:00', 'Changed', '2026-01-02T00:00:00.0000000+00:00');
    INSERT INTO file_versions (snapshot_id, relative_path, content_hash, size, source_modified_at, change_kind, recorded_at, quick_hash, quick_hash_scheme)
        VALUES (1, 'C\other.txt', 'cc33', 5, '2026-01-01T00:00:00.0000000+00:00', 'Added', '2026-01-01T00:00:00.0000000+00:00', 'qq', 1);
    """

let createLegacy (path: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
    use connection = new SqliteConnection $"Data Source={path};Pooling=False"
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- legacySchema
    command.ExecuteNonQuery() |> ignore

module ``Manifest storage`` =
    [<Fact>]
    let ``Rows and snapshot outcomes round trip into the current state`` () =
        TempDir.using (fun root ->
            let handle = SqliteManifest.openAt (database root) true

            write
                handle
                [ regular "C\\a.txt" "h1" 10L ChangeKind.Added 1
                  regular "C\\gone.txt" "h2" 3L ChangeKind.Added 1
                  regular "C\\gone.txt" "h2" 3L ChangeKind.Deleted 2
                  linked "C\\l" (ValueSome "target") ChangeKind.Linked 1 ]
            |> ignore

            let state = SqliteManifest.currentState handle
            Assert.Equal(2, state.Count)

            match state["C\\a.txt"] with
            | TrackedState.Regular content ->
                Assert.Equal("h1", ContentHash.value content.Hash)
                Assert.Equal(10L, content.Size)
                Assert.True(content.SourceModifiedAt.Equals(at 5))
                Assert.Equal(ValueSome { QuickHash = "qh1"; QuickScheme = 1 }, content.Quick)
            | _ -> Assert.Fail "expected regular content"

            Assert.True(match state["C\\l"] with TrackedState.Linked(ValueSome "target") -> true | _ -> false)
            SqliteManifest.close handle)

    [<Fact>]
    let ``A target path with connection-string punctuation opens for writing and for reading`` () =
        TempDir.using (fun root ->
            let target = Path.Combine(root, "backup;drive=E")
            let handle = SqliteManifest.openAt (database target) true
            write handle [ regular "C\\a.txt" "h1" 10L ChangeKind.Added 1 ] |> ignore
            SqliteManifest.close handle

            let reader = SqliteManifest.openReadOnly (database target)
            Assert.True((SqliteManifest.currentState reader).ContainsKey "C\\a.txt")
            SqliteManifest.close reader

            // Closing released the file: it can be deleted at once.
            File.Delete(database target))

    [<Fact>]
    let ``Lookups in the current state ignore letter case`` () =
        TempDir.using (fun root ->
            let handle = SqliteManifest.openAt (database root) true
            write handle [ regular "C\\Photo.JPG" "h1" 1L ChangeKind.Added 1 ] |> ignore
            Assert.True((SqliteManifest.currentState handle).ContainsKey "c\\photo.jpg")
            SqliteManifest.close handle)

    [<Fact>]
    let ``State as recorded at a time`` () =
        TempDir.using (fun root ->
            let handle = SqliteManifest.openAt (database root) true
            write handle [ regular "C\\a.txt" "h1" 1L ChangeKind.Added 1; regular "C\\a.txt" "h2" 2L ChangeKind.Changed 9 ] |> ignore
            let state = SqliteManifest.currentState handle
            Assert.Equal("h2", (match state["C\\a.txt"] with TrackedState.Regular c -> ContentHash.value c.Hash | _ -> ""))
            SqliteManifest.close handle)

module ``Manifest batches`` =
    [<Fact>]
    let ``A checkpoint makes written rows durable, and discarding drops only what came after`` () =
        TempDir.using (fun root ->
            let handle = SqliteManifest.openAt (database root) true
            let snapshot = SqliteManifest.beginSnapshot handle (at 0)
            let mutable batch = SqliteManifest.beginBatch handle
            SqliteManifest.record batch snapshot (regular "C\\kept" "h1" 1L ChangeKind.Added 1)
            batch <- SqliteManifest.checkpoint batch
            SqliteManifest.record batch snapshot (regular "C\\lost" "h2" 1L ChangeKind.Added 1)
            SqliteManifest.discard batch
            let state = SqliteManifest.currentState handle
            Assert.True(state.ContainsKey "C\\kept")
            Assert.False(state.ContainsKey "C\\lost")
            SqliteManifest.close handle)

    [<Fact>]
    let ``A checkpoint truncates the write-ahead log`` () =
        TempDir.using (fun root ->
            let path = database root
            let handle = SqliteManifest.openAt path true
            let snapshot = SqliteManifest.beginSnapshot handle (at 0)
            let mutable batch = SqliteManifest.beginBatch handle

            for i in 0..50 do
                SqliteManifest.record batch snapshot (regular $"C\\f{i}" "h" 1L ChangeKind.Added 1)

            batch <- SqliteManifest.checkpoint batch
            let wal = path + "-wal"
            Assert.True(not (File.Exists wal) || FileInfo(wal).Length = 0L)
            SqliteManifest.discard batch
            SqliteManifest.close handle)

    [<Fact>]
    let ``Snapshots still running are reconciled as failed`` () =
        TempDir.using (fun root ->
            let path = database root
            let handle = SqliteManifest.openAt path true
            SqliteManifest.beginSnapshot handle (at 0) |> ignore
            SqliteManifest.reconcileIncompleteSnapshots handle (at 7)
            SqliteManifest.close handle
            use connection = new SqliteConnection $"Data Source={path};Pooling=False"
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT status FROM snapshots"
            Assert.Equal("Failed", string (command.ExecuteScalar())))

module ``Manifest compatibility`` =
    [<Fact>]
    let ``A legacy-schema database migrates to the rows that the C# repository recorded`` () =
        TempDir.using (fun root ->
            let path = Path.Combine(root, "fs", "profile.db")
            createLegacy path
            SqliteManifest.close (SqliteManifest.openAt path true)
            let recorded = Corpus.read "manifest" "legacy-migration.txt"
            Corpus.assertSame [ ("legacy migration", recorded, String.Join("\n", dumpRows path)) ]
            Assert.Equal(3, List.length (dumpRows path))

            // The migrated table is case-insensitive: it now lists the casings as one path.
            let handle = SqliteManifest.openAt path true
            Assert.Equal(2, (SqliteManifest.currentState handle).Count)
            SqliteManifest.close handle)

    [<Fact>]
    let ``Rows written by the F# manifest read back with their content and link state`` () =
        TempDir.using (fun root ->
            let path = database root
            let handle = SqliteManifest.openAt path true

            write
                handle
                [ regular "C\\a.txt" "h1" 10L ChangeKind.Added 1
                  linked "C\\l" (ValueSome "t") ChangeKind.Linked 1
                  linked "C\\gone-link" ValueNone ChangeKind.Deleted 1 ]
            |> ignore

            SqliteManifest.close handle
            let reader = SqliteManifest.openAt path false
            let state = SqliteManifest.currentState reader
            Assert.Equal(2, state.Count)

            match state["C\\a.txt"] with
            | TrackedState.Regular content ->
                Assert.Equal("h1", ContentHash.value content.Hash)
                Assert.Equal(10L, content.Size)
            | TrackedState.Linked _ -> Assert.Fail "expected regular content"

            match state["C\\l"] with
            | TrackedState.Linked target -> Assert.Equal(ValueSome "t", target)
            | TrackedState.Regular _ -> Assert.Fail "expected a link"

            SqliteManifest.close reader
            SqliteConnection.ClearAllPools())

    [<Fact>]
    let ``A row with an unknown change kind is refused when the state is read`` () =
        TempDir.using (fun root ->
            let path = database root
            SqliteManifest.close (SqliteManifest.openAt path true)

            let insertUnknownKind () =
                use connection = new SqliteConnection $"Data Source={path};Pooling=False"
                connection.Open()
                use command = connection.CreateCommand()

                command.CommandText <-
                    """
                    INSERT INTO snapshots (started_at, status) VALUES ('2026-01-01T00:00:00.0000000+00:00', 'Complete');
                    INSERT INTO file_versions (snapshot_id, relative_path, content_hash, size, source_modified_at, change_kind, recorded_at)
                        VALUES (1, 'C\a.txt', 'aa11', 10, '2026-01-01T00:00:00.0000000+00:00', 'Teleported', '2026-01-01T00:00:00.0000000+00:00');
                    """

                command.ExecuteNonQuery() |> ignore

            insertUnknownKind ()
            let handle = SqliteManifest.openAt path false
            Assert.Throws<InvalidDataException>(fun () -> SqliteManifest.currentState handle |> ignore) |> ignore
            SqliteManifest.close handle
            SqliteConnection.ClearAllPools())

    [<Fact>]
    let ``Opening a missing database read-only creates no file`` () =
        TempDir.using (fun root ->
            let path = database root
            let handle = SqliteManifest.openAt path false
            Assert.Equal(0, (SqliteManifest.currentState handle).Count)
            Assert.False(File.Exists path)
            Assert.False(Directory.Exists(Path.GetDirectoryName path |> nonNull))
            // A write has no database to go to.
            Assert.Throws<InvalidOperationException>(fun () -> SqliteManifest.beginSnapshot handle (at 0) |> ignore) |> ignore
            SqliteManifest.close handle)

module ``Incremental change detection`` =
    [<Fact>]
    let ``Multiple historically recorded casings of the same path`` () =
        TempDir.using (fun root ->
            let path = database root
            createLegacy path
            // Rows were recorded under two casings before the case-insensitive collation existed.
            let handle = SqliteManifest.openAt path true
            let state = SqliteManifest.currentState handle

            match state["C\\PHOTO.jpg"] with
            | TrackedState.Regular content -> Assert.Equal("bb22", ContentHash.value content.Hash) // the most recent row
            | _ -> Assert.Fail "expected regular content"

            Assert.Equal(2, state.Count)
            // Asking again, and from a fresh connection, gives the same answer.
            SqliteManifest.close handle
            let again = SqliteManifest.openAt path true
            Assert.Equal(2, (SqliteManifest.currentState again).Count)
            SqliteManifest.close again)

    let private stateOf (path: string) : TrackedPaths =
        let handle = SqliteManifest.openAt path true

        try
            SqliteManifest.currentState handle
        finally
            SqliteManifest.close handle

    [<Fact>]
    let ``Several recorded casings of a non-ASCII path`` () =
        for older, newer in [ "Äiti.txt", "äiti.txt"; "äiti.txt", "Äiti.txt" ] do
            TempDir.using (fun root ->
                let path = database root
                let handle = SqliteManifest.openAt path true
                write handle [ regular older "aa11" 10L ChangeKind.Added 1 ] |> ignore
                write handle [ regular newer "bb22" 11L ChangeKind.Changed 2 ] |> ignore
                SqliteManifest.close handle

                // Asked twice, from fresh connections: the same single entry, spelled as the newer row spells it.
                for _ in 1..2 do
                    let state = stateOf path
                    Assert.Equal(1, state.Count)
                    Assert.Equal<string list>([ newer ], List.ofSeq state.Keys)

                    match state[older] with
                    | TrackedState.Regular content -> Assert.Equal("bb22", ContentHash.value content.Hash)
                    | _ -> Assert.Fail "expected regular content")

    [<Fact>]
    let ``Deletion recorded under another casing of a non-ASCII path`` () =
        TempDir.using (fun root ->
            let path = database root
            let handle = SqliteManifest.openAt path true
            write handle [ regular "Äiti.txt" "aa11" 10L ChangeKind.Added 1 ] |> ignore
            write handle [ regular "äiti.txt" "bb22" 11L ChangeKind.Changed 2 ] |> ignore
            write handle [ regular "äiti.txt" "bb22" 11L ChangeKind.Deleted 3 ] |> ignore
            SqliteManifest.close handle

            let state = stateOf path
            Assert.Equal(0, state.Count)
            Assert.False(state.ContainsKey "Äiti.txt"))
