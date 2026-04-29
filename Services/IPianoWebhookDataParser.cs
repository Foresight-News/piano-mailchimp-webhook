using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public interface IPianoWebhookDataParser
{
    PianoWebhookEvent Parse(string encryptedData);
}
