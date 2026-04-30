namespace piano_mailchimp_webhook.Config;

public sealed class PianoOptions
{
    public const string SectionName = "Piano";

    public string BaseUrl { get; init; } = "https://api.piano.io";
    public string ApiToken { get; init; } = string.Empty;
    public string ApplicationId { get; init; } = string.Empty;
    public string PrivateKey { get; init; } = string.Empty;
}
