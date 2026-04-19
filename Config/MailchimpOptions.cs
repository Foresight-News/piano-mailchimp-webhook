namespace piano_mailchimp_webhook.Config;

public sealed class MailchimpOptions
{
    public const string SectionName = "Mailchimp";

    public string ApiKey { get; init; } = string.Empty;
    public string ServerPrefix { get; init; } = string.Empty;
    public string AudienceId { get; init; } = string.Empty;
}
