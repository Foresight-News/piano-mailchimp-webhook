namespace piano_mailchimp_webhook.Models;

public sealed class PianoWebhookEventRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public string? DeduplicationKey { get; init; }
    public string? Event { get; init; }
    public string? Uid { get; init; }
    public string? Aid { get; init; }
    public string? Timestamp { get; init; }
    public string RawPayload { get; init; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
}
