/// The manifest's recorded schema version. Each module is named after a requirement of
/// openspec/specs/manifest-versioning/spec.md and each test after one of its scenarios, except where a test says
/// otherwise.
module Vara.Engine.Tests.ManifestVersioningTests

open System.IO
open Microsoft.Data.Sqlite
open Xunit
open Vara.Engine
open Vara.Engine.Tests
open Vara.Engine.Tests.SqliteManifestTests
open Vara.Testing

let private databaseIn (root: string) : string = Path.Combine(root, "fs", "profile.db")

let private open' (path: string) : SqliteConnection =
    let connection = new SqliteConnection $"Data Source={path};Pooling=False"
    connection.Open()
    connection

let private versionOf (path: string) : int =
    use connection = open' path
    ManifestSchema.readVersion connection

let private setVersion (path: string) (version: int) : unit =
    use connection = open' path
    use command = connection.CreateCommand()
    command.CommandText <- $"PRAGMA user_version = {version};"
    command.ExecuteNonQuery() |> ignore

let private tableNames (path: string) : string list =
    use connection = open' path
    use command = connection.CreateCommand()
    command.CommandText <- "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name"
    use reader = command.ExecuteReader()
    [ while reader.Read() do
          yield reader.GetString 0 ]

module ``The manifest records a schema version`` =
    [<Fact>]
    let ``New database is stamped`` () =
        TempDir.using (fun root ->
            let path = databaseIn root
            SqliteManifest.close (SqliteManifest.openAt path true)
            Assert.Equal(1, versionOf path))

module ``An unversioned manifest is version 1`` =
    [<Fact>]
    let ``Unversioned manifest is stamped on a backup`` () =
        TempDir.using (fun root ->
            // A current-schema database as every one written before versioning looks: tables, no recorded version.
            let path = databaseIn root
            SqliteManifest.close (SqliteManifest.openAt path true)
            setVersion path 0
            let before = dumpRows path

            SqliteManifest.close (SqliteManifest.openAt path true)

            Assert.Equal(1, versionOf path)
            Assert.Equal<string list>(before, dumpRows path))

    [<Fact>]
    let ``Pre-collation manifest is migrated and stamped`` () =
        TempDir.using (fun root ->
            let path = databaseIn root
            createLegacy path
            Assert.Equal(0, versionOf path)

            SqliteManifest.close (SqliteManifest.openAt path true)

            Assert.Equal(1, versionOf path)
            Assert.Equal(3, List.length (dumpRows path)))

    [<Fact>]
    let ``Read-only command leaves an unversioned manifest as it is`` () =
        TempDir.using (fun root ->
            let path = databaseIn root
            SqliteManifest.close (SqliteManifest.openAt path true)
            setVersion path 0
            let before = File.ReadAllBytes path

            let handle = SqliteManifest.openReadOnly path

            let count =
                try
                    (SqliteManifest.currentState handle).Count
                finally
                    SqliteManifest.close handle

            Assert.Equal(0, count)

            Assert.Equal<byte array>(before, File.ReadAllBytes path)
            Assert.Equal(0, versionOf path))

module ``A newer manifest is refused`` =
    let private refusal (action: unit -> unit) : ManifestSchemaTooNew =
        Assert.Throws<ManifestSchemaTooNew>(action)

    [<Fact>]
    let ``A writable open refuses it and leaves the file as it was`` () =
        TempDir.using (fun root ->
            let path = databaseIn root
            SqliteManifest.close (SqliteManifest.openAt path true)
            setVersion path 99
            let before = File.ReadAllBytes path

            let error = refusal (fun () -> SqliteManifest.openAt path true |> ignore)

            Assert.Equal(99, error.Found)
            Assert.Equal(ManifestSchema.currentVersion, error.Supported)
            Assert.Contains("99", error.Message)
            Assert.Equal<byte array>(before, File.ReadAllBytes path)
            // The refused connection was released: the file can be deleted.
            File.Delete path)

    [<Fact>]
    let ``A read-only open refuses it and leaves the file as it was`` () =
        TempDir.using (fun root ->
            let path = databaseIn root
            SqliteManifest.close (SqliteManifest.openAt path true)
            setVersion path 99
            let before = File.ReadAllBytes path

            let error = refusal (fun () -> SqliteManifest.openReadOnly path |> ignore)

            Assert.Equal(99, error.Found)
            Assert.Equal<byte array>(before, File.ReadAllBytes path)
            File.Delete path)

module ``Older versions migrate in order`` =
    let private current (path: string) : SqliteConnection =
        SqliteManifest.close (SqliteManifest.openAt path true)
        open' path

    [<Fact>]
    let ``Steps apply in order and each records its version`` () =
        TempDir.using (fun root ->
            let path = databaseIn root
            use connection = current path

            ManifestSchema.initializeWith
                [ (1, "CREATE TABLE added_in_two (a INTEGER);"); (2, "CREATE TABLE added_in_three (a INTEGER);") ]
                3
                connection

            Assert.Equal(3, ManifestSchema.readVersion connection)
            Assert.Contains("added_in_two", tableNames path)
            Assert.Contains("added_in_three", tableNames path))

    [<Fact>]
    let ``Interrupted migration leaves the previous version`` () =
        TempDir.using (fun root ->
            let path = databaseIn root
            use connection = current path
            let before = tableNames path

            // The second statement fails, so the first must not survive.
            let failing = [ (1, "CREATE TABLE half_done (a INTEGER); INSERT INTO no_such_table VALUES (1);") ]

            Assert.Throws<SqliteException>(fun () -> ManifestSchema.initializeWith failing 2 connection) |> ignore

            Assert.Equal(1, ManifestSchema.readVersion connection)
            Assert.Equal<string list>(before, tableNames path))

    [<Fact>]
    let ``A missing step is reported, not skipped`` () =
        TempDir.using (fun root ->
            let path = databaseIn root
            use connection = current path

            Assert.Throws<System.InvalidOperationException>(fun () -> ManifestSchema.initializeWith [] 2 connection)
            |> ignore

            Assert.Equal(1, ManifestSchema.readVersion connection))
