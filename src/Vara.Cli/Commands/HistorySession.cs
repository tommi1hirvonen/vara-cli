using Vara.Application.History;
using Vara.Application.Profiles;
using Vara.Cli.Composition;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;

namespace Vara.Cli.Commands;

/// <summary>
/// The read-only view of one profile that the history-browsing commands (<c>browse</c>,
/// <c>history</c>, <c>show</c>, <c>diff</c>, <c>deleted</c>, <c>snapshot(s)</c>) all open:
/// the resolved profile, its backing services - created without side effects for a profile
/// that has never completed a backup - and the history services (<see cref="SnapshotHistoryService"/>, <see cref="FileVersionService"/>,
/// <see cref="DirectoryRestoreService"/>) over them.
/// </summary>
internal sealed class HistorySession : IDisposable
{
    private readonly ProfileServices _services;

    private HistorySession(Profile profile, ProfileServices services)
    {
        Profile = profile;
        _services = services;
        History = new SnapshotHistoryService(services.Repository);
        FileVersions = new FileVersionService(services.Repository, services.ContentStore);
        DirectoryRestore = new DirectoryRestoreService(services.Repository, services.ContentStore);
    }

    public Profile Profile { get; }

    public SnapshotHistoryService History { get; }

    public FileVersionService FileVersions { get; }

    public DirectoryRestoreService DirectoryRestore { get; }

    public Func<string, bool> IsWithinMirror => _services.ContentStore.IsWithinMirror;

    public static HistorySession Open(
        ProfileResolver profileResolver,
        ProfileServiceFactory serviceFactory,
        string? profileName,
        string? configPath)
    {
        var profile = profileResolver.ResolveForBrowsing(profileName, configPath);
        return new HistorySession(profile, serviceFactory.CreateFor(profile, createIfMissing: false));
    }

    /// <summary>
    /// Resolves a user-typed file path (mirror-relative, absolute source path, or relative to
    /// the current directory) to the mirror-relative path with recorded history; when no
    /// interpretation matches, the input is returned unchanged so the history lookup reports it.
    /// </summary>
    public string ResolveFilePath(string input) =>
        TryResolveFilePath(input, out var resolved) ? resolved : input;

    public bool TryResolveFilePath(string input, out string resolvedPath) =>
        SnapshotPathResolver.TryResolve(
            Profile.TargetRoot,
            input,
            candidate => _services.Repository.GetFileHistory(candidate).Count > 0,
            _services.ContentStore.IsWithinMirror,
            out resolvedPath);

    public DirectoryResolution ResolveDirectory(string input) =>
        DirectoryArgumentResolver.Resolve(
            Profile.TargetRoot,
            input,
            _services.Repository,
            _services.ContentStore.IsWithinMirror);

    public void Dispose() => _services.Dispose();
}
