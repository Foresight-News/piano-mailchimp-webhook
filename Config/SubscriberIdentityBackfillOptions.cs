namespace piano_mailchimp_webhook.Config;

public sealed class SubscriberIdentityBackfillOptions
{
    public const string SectionName = "SubscriberIdentityBackfill";

    public string MappingCsvPath { get; init; } = string.Empty;

    public string MappingCsvContent { get; init; } = string.Empty;

    public string PianoIdMergeFieldName { get; init; } = "PIANOID";

    public bool DryRun { get; init; } = true;
}
