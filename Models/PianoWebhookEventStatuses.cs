namespace piano_mailchimp_webhook.Models;

public static class PianoWebhookEventStatuses
{
    public const string Received = "Received";
    public const string Duplicate = "Duplicate";
    public const string Processed = "Processed";
    public const string Failed = "Failed";
    public const string InvalidPayload = "InvalidPayload";
}
