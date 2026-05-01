namespace piano_mailchimp_webhook.Models;

public sealed class PaidAccessReconciliationSummary
{
    public int Scanned { get; set; }

    public int ActiveAccess { get; set; }

    public int AddedExpiredTag { get; set; }

    public int WouldAddExpiredTag { get; set; }

    public int MissingPianoId { get; set; }

    public int Failed { get; set; }
}
