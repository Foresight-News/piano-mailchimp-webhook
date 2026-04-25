using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using piano_mailchimp_webhook.Models;
using piano_mailchimp_webhook.Services;

namespace piano_mailchimp_webhook.Controllers;

[ApiController]
[Route("webhooks/piano")]
public sealed class PianoWebhookController(
    IPianoWebhookEventStore eventStore,
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
            webhookEvent = DeserializeWebhookEvent(rawPayload, Request.ContentType);
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

        if (string.Equals(storedEvent.Status, PianoWebhookEventStatuses.Duplicate, StringComparison.Ordinal))
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

    private static PianoWebhookEvent? DeserializeWebhookEvent(string rawPayload, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return null;
        }

        if (IsJsonPayload(rawPayload, contentType))
        {
            return JsonSerializer.Deserialize<PianoWebhookEvent>(rawPayload, JsonOptions);
        }

        var values = QueryHelpers.ParseQuery(rawPayload);

        return new PianoWebhookEvent
        {
            Type = GetFormValue(values, "type"),
            Event = GetFormValue(values, "event"),
            Uid = GetFormValue(values, "uid"),
            Aid = GetFormValue(values, "aid"),
            Timestamp = GetFormValue(values, "timestamp"),
            UpdatedCustomFields =
                GetFormValue(values, "Updated_custom_fields") ??
                GetFormValue(values, "updated_custom_fields")
        };
    }

    private static bool IsJsonPayload(string rawPayload, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType) &&
            contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return rawPayload.TrimStart().StartsWith('{');
    }

    private static string? GetFormValue(
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> values,
        string key)
    {
        return values.TryGetValue(key, out var value)
            ? value.FirstOrDefault()
            : null;
    }
}
