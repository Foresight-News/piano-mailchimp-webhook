using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public sealed class PianoWebhookEventStore(IHostEnvironment hostEnvironment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _storageDirectory = Path.Combine(
        hostEnvironment.ContentRootPath,
        "App_Data",
        "PianoWebhookEvents");

    public async Task<PianoWebhookEventRecord> SaveReceivedAsync(
        PianoWebhookEvent? webhookEvent,
        string rawPayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawPayload);

        await Gate.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(_storageDirectory);

            var deduplicationKey = BuildDeduplicationKey(webhookEvent);
            var receivedAt = DateTimeOffset.UtcNow;
            var isDuplicate = !string.IsNullOrWhiteSpace(deduplicationKey) &&
                await HasExistingRecordAsync(deduplicationKey, cancellationToken);

            var record = new PianoWebhookEventRecord
            {
                DeduplicationKey = deduplicationKey,
                Event = webhookEvent?.Event?.Trim(),
                Uid = webhookEvent?.Uid?.Trim(),
                Aid = webhookEvent?.Aid?.Trim(),
                Timestamp = webhookEvent?.Timestamp?.Trim(),
                RawPayload = rawPayload,
                ReceivedAt = receivedAt,
                ProcessedAt = isDuplicate ? receivedAt : null,
                Status = isDuplicate ? "Duplicate" : "Received",
                ErrorMessage = isDuplicate ? "Duplicate webhook ignored." : null
            };

            await WriteRecordAsync(record, cancellationToken);

            return record;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task MarkProcessedAsync(string recordId, CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(recordId, "Processed", null, cancellationToken);
    }

    public async Task MarkFailedAsync(
        string recordId,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(recordId, "Failed", errorMessage, cancellationToken);
    }

    public async Task MarkInvalidPayloadAsync(
        string recordId,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(recordId, "InvalidPayload", errorMessage, cancellationToken);
    }

    private async Task UpdateStatusAsync(
        string recordId,
        string status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        await Gate.WaitAsync(cancellationToken);

        try
        {
            var path = GetRecordPath(recordId);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Webhook event record {recordId} was not found.", path);
            }

            PianoWebhookEventRecord? existing;
            await using (var inputStream = File.OpenRead(path))
            {
                existing = await JsonSerializer.DeserializeAsync<PianoWebhookEventRecord>(
                    inputStream,
                    JsonOptions,
                    cancellationToken);
            }

            if (existing is null)
            {
                throw new InvalidOperationException($"Webhook event record {recordId} could not be read.");
            }

            var updated = new PianoWebhookEventRecord
            {
                Id = existing.Id,
                DeduplicationKey = existing.DeduplicationKey,
                Event = existing.Event,
                Uid = existing.Uid,
                Aid = existing.Aid,
                Timestamp = existing.Timestamp,
                RawPayload = existing.RawPayload,
                ReceivedAt = existing.ReceivedAt,
                ProcessedAt = DateTimeOffset.UtcNow,
                Status = status,
                ErrorMessage = errorMessage
            };

            await WriteRecordAsync(updated, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<bool> HasExistingRecordAsync(string deduplicationKey, CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(_storageDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            await using var inputStream = File.OpenRead(path);
            var existing = await JsonSerializer.DeserializeAsync<PianoWebhookEventRecord>(
                inputStream,
                JsonOptions,
                cancellationToken);

            if (existing is null)
            {
                continue;
            }

            if (string.Equals(existing.DeduplicationKey, deduplicationKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task WriteRecordAsync(PianoWebhookEventRecord record, CancellationToken cancellationToken)
    {
        var path = GetRecordPath(record.Id);

        await using var outputStream = File.Create(path);
        await JsonSerializer.SerializeAsync(outputStream, record, JsonOptions, cancellationToken);
    }

    private string GetRecordPath(string recordId)
    {
        return Path.Combine(_storageDirectory, $"{recordId}.json");
    }

    private static string? BuildDeduplicationKey(PianoWebhookEvent? webhookEvent)
    {
        var eventName = webhookEvent?.Event?.Trim();
        var uid = webhookEvent?.Uid?.Trim();
        var timestamp = webhookEvent?.Timestamp?.Trim();

        if (string.IsNullOrWhiteSpace(eventName) ||
            string.IsNullOrWhiteSpace(uid) ||
            string.IsNullOrWhiteSpace(timestamp))
        {
            return null;
        }

        var rawKey = $"{uid}\n{eventName}\n{timestamp}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
