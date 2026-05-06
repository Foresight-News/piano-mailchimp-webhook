using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public interface IPaidAccessReconciliationService
{
    Task<PaidAccessReconciliationSummary> ReconcileAsync(
        PaidAccessReconciliationRequest? request = null,
        CancellationToken cancellationToken = default);
}
