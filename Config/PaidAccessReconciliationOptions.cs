namespace piano_mailchimp_webhook.Config;

public sealed class PaidAccessReconciliationOptions
{
    public const string SectionName = "PaidAccessReconciliation";

    public string PaidTagName { get; init; } = "PAID";
    public string ExpiredTagName { get; init; } = "EXPIRED";

    public string PaidTagSegmentId { get; init; } = string.Empty;

    public int BatchSize { get; init; } = 100;

    public bool DryRun { get; init; } = true;
}
