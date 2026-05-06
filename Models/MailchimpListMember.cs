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
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        if (!TryGetMergeFieldValue(fieldName.Trim(), out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }

    private bool TryGetMergeFieldValue(string fieldName, out JsonElement value)
    {
        if (MergeFields.TryGetValue(fieldName, out value))
        {
            return true;
        }

        foreach (var mergeField in MergeFields)
        {
            if (string.Equals(mergeField.Key, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                value = mergeField.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
