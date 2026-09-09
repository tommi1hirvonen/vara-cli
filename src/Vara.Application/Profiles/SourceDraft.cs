using Vara.Core.Configuration;

namespace Vara.Application.Profiles;

/// <summary>
/// A mutable, in-progress editable representation of a single <see cref="Source"/>'s
/// fields, used by the interactive profile editor. Unlike <see cref="Source"/> itself
/// (an immutable record whose constructor enforces all structural invariants), a
/// <see cref="SourceDraft"/> can represent a state <see cref="Source"/>'s constructor
/// would reject (for example, a missing or relative path) so the field-by-field editing
/// screen always has something to display, even mid-edit.
/// </summary>
public sealed class SourceDraft
{
    public string? Path { get; set; }
    public bool Recursive { get; set; } = true;
    public List<string> Excludes { get; } = [];
    public List<string> IncludeGlobs { get; } = [];
    public List<string> ExcludeGlobs { get; } = [];

    /// <summary>Initializes a draft reproducing an existing <see cref="Source"/>'s values.</summary>
    public static SourceDraft FromSource(Source source)
    {
        var draft = new SourceDraft { Path = source.Path, Recursive = source.Recursive };
        draft.Excludes.AddRange(source.Excludes);
        draft.IncludeGlobs.AddRange(source.IncludeGlobs);
        draft.ExcludeGlobs.AddRange(source.ExcludeGlobs);
        return draft;
    }

    /// <summary>
    /// Attempts to construct a <see cref="Source"/> from the draft's current field
    /// values, using <see cref="Source"/>'s own constructor as the single source of truth
    /// for what is valid. On failure, <paramref name="error"/> is the domain constructor's
    /// exception message, verbatim - the draft layer never re-implements a validation rule
    /// like "path must be absolute", it only decides when to ask.
    /// </summary>
    public bool TryBuild(out Source? source, out string? error)
    {
        try
        {
            source = new Source(Path ?? string.Empty, Recursive, Excludes, IncludeGlobs, ExcludeGlobs);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            source = null;
            error = ex.Message;
            return false;
        }
    }
}
