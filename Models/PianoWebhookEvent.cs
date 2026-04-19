using System.Text.Json.Serialization;

namespace piano_mailchimp_webhook.Models;

public sealed class PianoWebhookEvent
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("uid")]
    public string? Uid { get; init; }

    [JsonPropertyName("aid")]
    public string? Aid { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    [JsonPropertyName("Updated_custom_fields")]
    public string? UpdatedCustomFields { get; init; }

    public string[] GetUpdatedCustomFields()
    {
        if (string.IsNullOrWhiteSpace(UpdatedCustomFields))
        {
            return [];
        }

        return UpdatedCustomFields
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }
}
