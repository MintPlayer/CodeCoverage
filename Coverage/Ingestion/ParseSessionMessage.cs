using MintPlayer.Spark.Messaging.Abstractions;

namespace Coverage.Ingestion;

/// <summary>
/// Queued when an upload lands; processed by <see cref="ParseSessionRecipient"/>.
/// Explicit queue name — never rely on the FullName fallback.
/// </summary>
[MessageQueue("coverage-parse-session")]
public record ParseSessionMessage
{
    public required string BuildId { get; init; }
    public required string SessionId { get; init; }
}
