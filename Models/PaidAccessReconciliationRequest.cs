using System.Text.Json.Serialization;

namespace piano_mailchimp_webhook.Models;

public sealed class PaidAccessReconciliationRequest
{
    [JsonPropertyName("offset")]
    public int? Offset { get; init; }

    [JsonPropertyName("limit")]
    public int? Limit { get; init; }
}
