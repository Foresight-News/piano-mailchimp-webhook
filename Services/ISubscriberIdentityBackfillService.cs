using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public interface ISubscriberIdentityBackfillService
{
    Task<SubscriberIdentityBackfillSummary> BackfillAsync(
        SubscriberIdentityBackfillRequest? request = null,
        CancellationToken cancellationToken = default);
}
