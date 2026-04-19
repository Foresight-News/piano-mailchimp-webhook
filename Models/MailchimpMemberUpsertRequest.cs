using System.Text.Json.Serialization;

namespace piano_mailchimp_webhook.Models;

public sealed class MailchimpMemberUpsertRequest
{
    [JsonPropertyName("email_address")]
    public string EmailAddress { get; init; } = string.Empty;

    [JsonPropertyName("status_if_new")]
    public string? StatusIfNew { get; init; }

    [JsonPropertyName("merge_fields")]
    public Dictionary<string, object?> MergeFields { get; init; } = [];

    [JsonPropertyName("interests")]
    public Dictionary<string, bool> Interests { get; init; } = [];
}
