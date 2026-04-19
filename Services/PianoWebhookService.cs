using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public sealed class PianoWebhookService(
    ILogger<PianoWebhookService> logger,
    IPianoWebhookProcessor webhookProcessor) : IPianoWebhookService
{
    public async Task ProcessAsync(PianoWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Received Piano webhook event {EventName} for uid {Uid}.",
            webhookEvent.Event ?? "unknown",
            webhookEvent.Uid ?? "unknown");

        await webhookProcessor.ProcessAsync(webhookEvent, cancellationToken);
    }
}
