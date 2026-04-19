namespace piano_mailchimp_webhook.Config;

public sealed class PianoWebhookOptions
{
    public const string SectionName = "PianoWebhook";

    public string SharedSecret { get; init; } = string.Empty;
    public string MailchimpAudienceId { get; init; } = string.Empty;
}
