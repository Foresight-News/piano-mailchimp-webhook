namespace piano_mailchimp_webhook.Models;

public sealed class PianoWebhookPayload
{
    public string? EventType { get; init; }
    public string? UserId { get; init; }
    public string? Email { get; init; }
}
