namespace Vara.Core.Configuration;

/// <summary>
/// Optional per-profile concurrency tuning for the backup pipeline's two independent
/// stages: the move-detection scan stage and the file-transfer stage. Each field is
/// <c>null</c> when not configured, distinct from a validated positive value - the
/// caller (see <see cref="Profile"/>) falls back to each stage's own default in that
/// case rather than treating <c>null</c> as a sentinel number. A provided value SHALL
/// be a positive whole number.
/// </summary>
public sealed record ConcurrencySettings
{
    public ConcurrencySettings(int? scanConcurrency, int? transferConcurrency)
    {
        if (scanConcurrency is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scanConcurrency), scanConcurrency, "Concurrency values must be positive.");
        }

        if (transferConcurrency is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transferConcurrency), transferConcurrency, "Concurrency values must be positive.");
        }

        ScanConcurrency = scanConcurrency;
        TransferConcurrency = transferConcurrency;
    }

    public int? ScanConcurrency { get; }
    public int? TransferConcurrency { get; }
}
