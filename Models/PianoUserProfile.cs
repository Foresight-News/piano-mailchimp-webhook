using System.Text.Json.Serialization;

namespace piano_mailchimp_webhook.Models;

public sealed class PianoUserProfile
{
    [JsonPropertyName("uid")]
    public string? Uid { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; init; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; init; }

    [JsonPropertyName("custom_fields")]
    public Dictionary<string, object?> CustomFields { get; init; } = [];
}
