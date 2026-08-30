namespace Vara.Core.Abstractions;

/// <summary>
/// Computes a fast, non-cryptographic content hash used for change detection and
/// content-addressed deduplication (not a security/integrity guarantee).
/// </summary>
public interface IHasher
{
    /// <summary>
    /// Computes the hash of the given stream's remaining content, as a lowercase hex string.
    /// </summary>
    string ComputeHash(Stream content);
}
