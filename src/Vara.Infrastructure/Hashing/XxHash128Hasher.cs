using System.IO.Hashing;
using Vara.Core.Abstractions;

namespace Vara.Infrastructure.Hashing;

/// <summary>
/// Computes content hashes with XxHash128, a fast non-cryptographic hash - appropriate
/// here because the requirement is accidental-duplication detection for change
/// detection and dedup, not an adversarial-integrity guarantee (see design.md).
/// </summary>
public sealed class XxHash128Hasher : IHasher
{
    public string ComputeHash(Stream content)
    {
        var hash = new XxHash128();
        hash.Append(content);
        return Convert.ToHexStringLower(hash.GetCurrentHash());
    }
}
