using Microsoft.Extensions.Options;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public sealed class PaidAccessReconciliationService(
    IMailchimpAudienceService mailchimpAudienceService,
    IPianoApiClient pianoApiClient,
    IOptions<PaidAccessReconciliationOptions> options,
    IOptions<SubscriberIdentityBackfillOptions> backfillOptions,
    ILogger<PaidAccessReconciliationService> logger) : IPaidAccessReconciliationService
{
    public async Task<PaidAccessReconciliationSummary> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        var reconciliationOptions = options.Value;
        ValidateOptions(reconciliationOptions);

        var summary = new PaidAccessReconciliationSummary();
        var offset = 0;

        while (true)
        {
            var page = await mailchimpAudienceService.ListSegmentMembersAsync(
                reconciliationOptions.PaidTagSegmentId,
                reconciliationOptions.BatchSize,
                offset,
                cancellationToken);

            foreach (var member in page.Members)
            {
                await ReconcileMemberAsync(member, reconciliationOptions, summary, cancellationToken);
            }

            offset += page.Members.Count;

            if (page.Members.Count == 0 || offset >= page.TotalItems)
            {
                break;
            }
        }

        logger.LogInformation(
            "Paid access reconciliation complete. Scanned: {Scanned}. ActiveAccess: {ActiveAccess}. RemovedPaidTag: {RemovedPaidTag}. WouldRemovePaidTag: {WouldRemovePaidTag}. MissingPianoId: {MissingPianoId}. Failed: {Failed}. DryRun: {DryRun}.",
            summary.Scanned,
            summary.ActiveAccess,
            summary.RemovedPaidTag,
            summary.WouldRemovePaidTag,
            summary.MissingPianoId,
            summary.Failed,
            reconciliationOptions.DryRun);

        return summary;
    }

    private async Task ReconcileMemberAsync(
        MailchimpListMember member,
        PaidAccessReconciliationOptions reconciliationOptions,
        PaidAccessReconciliationSummary summary,
        CancellationToken cancellationToken)
    {
        summary.Scanned++;

        if (string.IsNullOrWhiteSpace(member.EmailAddress))
        {
            summary.Failed++;
            logger.LogWarning("Skipping Mailchimp member because email_address is missing.");
            return;
        }

        var pianoUid = member.GetMergeFieldString(backfillOptions.Value.PianoIdMergeFieldName);
        if (string.IsNullOrWhiteSpace(pianoUid))
        {
            summary.MissingPianoId++;
            logger.LogWarning(
                "Skipping paid-access check for {EmailAddress} because {PianoIdMergeFieldName} is missing.",
                member.EmailAddress,
                backfillOptions.Value.PianoIdMergeFieldName);
            return;
        }

        try
        {
            var hasActiveAccess = await pianoApiClient.HasActiveAccessToAnyResourceAsync(
                pianoUid.Trim(),
                cancellationToken);

            if (hasActiveAccess)
            {
                summary.ActiveAccess++;
                return;
            }

            if (reconciliationOptions.DryRun)
            {
                summary.WouldRemovePaidTag++;
                logger.LogInformation(
                    "Dry run: would remove {PaidTagName} tag from {EmailAddress} for Piano uid {PianoUid}.",
                    reconciliationOptions.PaidTagName,
                    member.EmailAddress,
                    pianoUid);
                return;
            }

            await mailchimpAudienceService.RemoveMemberTagsAsync(
                member.EmailAddress,
                [reconciliationOptions.PaidTagName],
                cancellationToken);
            summary.RemovedPaidTag++;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            summary.Failed++;
            logger.LogError(
                exception,
                "Paid-access check failed for {EmailAddress} and Piano uid {PianoUid}.",
                member.EmailAddress,
                pianoUid);
        }
    }

    private static void ValidateOptions(PaidAccessReconciliationOptions reconciliationOptions)
    {
        if (string.IsNullOrWhiteSpace(reconciliationOptions.PaidTagSegmentId))
        {
            throw new InvalidOperationException(
                "PaidAccessReconciliation:PaidTagSegmentId is required.");
        }

        if (string.IsNullOrWhiteSpace(reconciliationOptions.PaidTagName))
        {
            throw new InvalidOperationException("PaidAccessReconciliation:PaidTagName is required.");
        }

        if (reconciliationOptions.BatchSize <= 0)
        {
            throw new InvalidOperationException(
                "PaidAccessReconciliation:BatchSize must be greater than zero.");
        }
    }
}
