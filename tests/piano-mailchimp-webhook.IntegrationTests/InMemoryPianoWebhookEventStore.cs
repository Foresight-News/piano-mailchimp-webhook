using System.Security.Cryptography;
using System.Text;
using piano_mailchimp_webhook.Models;
using piano_mailchimp_webhook.Services;

namespace piano_mailchimp_webhook.IntegrationTests;

internal sealed class InMemoryPianoWebhookEventStore : IPianoWebhookEventStore
{
    private readonly Lock _gate = new();
    private List<PianoWebhookEventRecord> _records = [];

    public Task<PianoWebhookEventRecord> SaveReceivedAsync(
        PianoWebhookEvent? webhookEvent,
        string rawPayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawPayload);

        lock (_gate)
        {
            var deduplicationKey = BuildDeduplicationKey(webhookEvent);
            var receivedAt = DateTimeOffset.UtcNow;
            var isDuplicate = !string.IsNullOrWhiteSpace(deduplicationKey) &&
                _records.Any(record => string.Equals(record.DeduplicationKey, deduplicationKey, StringComparison.Ordinal));

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
                Status = isDuplicate ? PianoWebhookEventStatuses.Duplicate : PianoWebhookEventStatuses.Received,
                ErrorMessage = isDuplicate ? "Duplicate webhook ignored." : null
            };

            _records.Add(record);
            return Task.FromResult(record);
        }
    }

    public Task MarkProcessedAsync(string recordId, CancellationToken cancellationToken = default)
    {
        UpdateStatus(recordId, PianoWebhookEventStatuses.Processed, null);
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(
        string recordId,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        UpdateStatus(recordId, PianoWebhookEventStatuses.Failed, errorMessage);
        return Task.CompletedTask;
    }

    public Task MarkInvalidPayloadAsync(
        string recordId,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        UpdateStatus(recordId, PianoWebhookEventStatuses.InvalidPayload, errorMessage);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PianoWebhookEventRecord>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<PianoWebhookEventRecord>>(
                _records.OrderBy(record => record.ReceivedAt).ToArray());
        }
    }

    private void UpdateStatus(string recordId, string status, string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        lock (_gate)
        {
            var existing = _records.SingleOrDefault(record => string.Equals(record.Id, recordId, StringComparison.Ordinal));
            if (existing is null)
            {
                throw new InvalidOperationException($"Webhook event record {recordId} was not found.");
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

            _records = _records
                .Select(record => string.Equals(record.Id, recordId, StringComparison.Ordinal) ? updated : record)
                .ToList();
        }
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
