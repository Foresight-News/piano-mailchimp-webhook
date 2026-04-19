namespace piano_mailchimp_webhook.Config;

public sealed class NewsletterFieldMapping
{
    public string PianoFieldName { get; init; } = string.Empty;
    public string MailchimpInterestId { get; init; } = string.Empty;
    public string? Description { get; init; }
}
