using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public interface IPianoWebhookEventStore
{
    Task<PianoWebhookEventRecord> SaveReceivedAsync(
        PianoWebhookEvent? webhookEvent,
        string rawPayload,
        CancellationToken cancellationToken = default);

    Task MarkProcessedAsync(string recordId, CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        string recordId,
        string errorMessage,
        CancellationToken cancellationToken = default);

    Task MarkInvalidPayloadAsync(
        string recordId,
        string errorMessage,
        CancellationToken cancellationToken = default);
}
