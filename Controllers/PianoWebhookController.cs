using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using piano_mailchimp_webhook.Models;
using piano_mailchimp_webhook.Services;

namespace piano_mailchimp_webhook.Controllers;

[ApiController]
[Route("webhooks/piano")]
public sealed class PianoWebhookController(
    PianoWebhookEventStore eventStore,
    IPianoWebhookProcessor webhookProcessor,
    ILogger<PianoWebhookController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        Request.EnableBuffering();

        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var rawPayload = await reader.ReadToEndAsync(cancellationToken);
        Request.Body.Position = 0;

        PianoWebhookEvent? webhookEvent = null;
        var storedEvent = default(PianoWebhookEventRecord);

        try
        {
            webhookEvent = JsonSerializer.Deserialize<PianoWebhookEvent>(rawPayload, JsonOptions);
        }
        catch (JsonException exception)
        {
            storedEvent = await eventStore.SaveReceivedAsync(null, rawPayload, cancellationToken);
            await eventStore.MarkInvalidPayloadAsync(storedEvent.Id, exception.Message, cancellationToken);

            logger.LogWarning(exception, "Received an invalid Piano webhook payload.");

            return BadRequest(new
            {
                success = false,
                error = "Invalid payload"
            });
        }

        storedEvent = await eventStore.SaveReceivedAsync(webhookEvent, rawPayload, cancellationToken);

        if (webhookEvent is null)
        {
            await eventStore.MarkInvalidPayloadAsync(storedEvent.Id, "Payload could not be deserialized.", cancellationToken);

            logger.LogWarning("Received a Piano webhook payload that could not be deserialized.");

            return BadRequest(new
            {
                success = false,
                error = "Invalid payload"
            });
        }

        logger.LogInformation(
            "Received Piano webhook event {EventName} for uid {Uid}.",
            webhookEvent.Event ?? "unknown",
            webhookEvent.Uid ?? "unknown");

        if (string.Equals(storedEvent.Status, "Duplicate", StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Skipping duplicate Piano webhook event {EventName} for uid {Uid}.",
                webhookEvent.Event ?? "unknown",
                webhookEvent.Uid ?? "unknown");

            return Ok(new
            {
                success = true,
                duplicate = true
            });
        }

        try
        {
            await webhookProcessor.ProcessAsync(webhookEvent, cancellationToken);
            await eventStore.MarkProcessedAsync(storedEvent.Id, cancellationToken);
        }
        catch (Exception exception)
        {
            await eventStore.MarkFailedAsync(storedEvent.Id, exception.Message, cancellationToken);

            logger.LogError(
                exception,
                "Processing failed for Piano webhook event {EventName} and uid {Uid}.",
                webhookEvent.Event ?? "unknown",
                webhookEvent.Uid ?? "unknown");

            throw;
        }

        return Ok(new
        {
            success = true
        });
    }
}
