# Deployment

`vara.exe` is published as a Native AOT executable and is portable: copy the single
`vara.exe` file to any location and run it - no other files or native libraries are
required alongside it.

## Minimum OS requirement

`vara.exe` uses the SQLite library built into Windows (`winsqlite3.dll`) instead of
shipping its own copy. This requires one of:

- Windows 10, version 1903 or later
- Windows 11
- Windows Server 2022 or later

Older Windows versions, and non-Windows operating systems, do not provide
`winsqlite3.dll` and cannot run `vara.exe`.

## Publishing

```
dotnet publish src/Vara.Cli/Vara.Cli.csproj -c Release -r win-x64
```

The publish output contains only `vara.exe` (plus optional `.pdb` symbol files) -
no `e_sqlite3.dll` or other native SQLite binary is produced or required.
