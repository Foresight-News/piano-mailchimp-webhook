namespace piano_mailchimp_webhook.Config;

public sealed class NewsletterMappingOptions
{
    public const string SectionName = "NewsletterMapping";

    public List<NewsletterFieldMapping> FieldMappings { get; init; } = [];
}
