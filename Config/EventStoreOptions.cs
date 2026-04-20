namespace piano_mailchimp_webhook.Config;

public sealed class EventStoreOptions
{
    public const string SectionName = "EventStore";

    public string ConnectionString { get; init; } = string.Empty;
    public string Schema { get; init; } = "dbo";
    public string TableName { get; init; } = "PianoWebhookEvents";
}
