using System.Text.Json.Serialization;

namespace piano_mailchimp_webhook.Models;

public sealed class MailchimpListMembersPage
{
    [JsonPropertyName("members")]
    public List<MailchimpListMember> Members { get; init; } = [];

    [JsonPropertyName("total_items")]
    public int TotalItems { get; init; }
}
