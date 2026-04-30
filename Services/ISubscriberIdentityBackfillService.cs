using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public interface ISubscriberIdentityBackfillService
{
    Task<SubscriberIdentityBackfillSummary> BackfillAsync(
        CancellationToken cancellationToken = default);
}
