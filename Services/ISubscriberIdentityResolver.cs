using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public interface ISubscriberIdentityResolver
{
    Task<SubscriberIdentityResolution> ResolveAsync(
        string email,
        CancellationToken cancellationToken = default);
}
