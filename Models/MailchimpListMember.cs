using System.Text.Json;
using System.Text.Json.Serialization;

namespace piano_mailchimp_webhook.Models;

public sealed class MailchimpListMember
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("email_address")]
    public string EmailAddress { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("merge_fields")]
    public Dictionary<string, JsonElement> MergeFields { get; init; } = [];

    public string? GetMergeFieldString(string fieldName)
    {
        if (!MergeFields.TryGetValue(fieldName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }
}
