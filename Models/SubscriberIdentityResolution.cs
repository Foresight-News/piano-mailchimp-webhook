namespace piano_mailchimp_webhook.Models;

public enum SubscriberIdentityResolutionStatus
{
    Found,
    NotFound,
    Ambiguous
}

public sealed record SubscriberIdentityResolution(
    SubscriberIdentityResolutionStatus Status,
    string? PianoUid = null)
{
    public static SubscriberIdentityResolution Found(string pianoUid) => new(
        SubscriberIdentityResolutionStatus.Found,
        pianoUid);

    public static SubscriberIdentityResolution NotFound { get; } = new(
        SubscriberIdentityResolutionStatus.NotFound);

    public static SubscriberIdentityResolution Ambiguous { get; } = new(
        SubscriberIdentityResolutionStatus.Ambiguous);
}
