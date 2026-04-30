using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public interface IPaidAccessReconciliationService
{
    Task<PaidAccessReconciliationSummary> ReconcileAsync(
        CancellationToken cancellationToken = default);
}
