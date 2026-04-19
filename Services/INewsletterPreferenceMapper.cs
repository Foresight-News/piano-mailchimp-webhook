using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public interface INewsletterPreferenceMapper
{
    Dictionary<string, bool> BuildInterestMap(PianoUserProfile user);
    bool AnyManagedFieldChanged(IReadOnlyList<string> updatedFields);
}
