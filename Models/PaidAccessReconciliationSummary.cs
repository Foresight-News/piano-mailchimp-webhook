namespace piano_mailchimp_webhook.Models;

public sealed class PaidAccessReconciliationSummary
{
    public int Scanned { get; set; }

    public int ActiveAccess { get; set; }

    public int RemovedPaidTag { get; set; }

    public int WouldRemovePaidTag { get; set; }

    public int MissingPianoId { get; set; }

    public int Failed { get; set; }
}
