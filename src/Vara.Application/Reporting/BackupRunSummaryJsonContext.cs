using System.Text.Json.Serialization;

namespace Vara.Application.Reporting;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for <see cref="BackupRunSummaryJson"/>,
/// so <c>vara backup --json</c> stays trim/Native-AOT safe (<c>Vara.Cli</c> is published with
/// <c>PublishAot</c>) instead of relying on reflection-based serialization.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupRunSummaryJson))]
public sealed partial class BackupRunSummaryJsonContext : JsonSerializerContext;
