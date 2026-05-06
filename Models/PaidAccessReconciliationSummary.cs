namespace piano_mailchimp_webhook.Models;

public sealed class PaidAccessReconciliationSummary
{
    public int Offset { get; set; }

    public int? Limit { get; set; }

    public int TotalItems { get; set; }

    public int NextOffset { get; set; }

    public bool HasMore { get; set; }

    public int Scanned { get; set; }

    public int ActiveAccess { get; set; }

    public int RemovedPaidTag { get; set; }

    public int WouldRemovePaidTag { get; set; }

    public int MissingPianoId { get; set; }

    public int Failed { get; set; }
}
