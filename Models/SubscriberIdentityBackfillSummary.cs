namespace piano_mailchimp_webhook.Models;

public sealed class SubscriberIdentityBackfillSummary
{
    public int Offset { get; set; }

    public int? Limit { get; set; }

    public int TotalItems { get; set; }

    public int NextOffset { get; set; }

    public bool HasMore { get; set; }

    public int Scanned { get; set; }

    public int AlreadyHadPianoId { get; set; }

    public int Updated { get; set; }

    public int WouldUpdate { get; set; }

    public int NotFound { get; set; }

    public int Ambiguous { get; set; }

    public int Failed { get; set; }
}
