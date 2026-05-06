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
        PaidAccessReconciliationRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var reconciliationOptions = options.Value;
        ValidateOptions(reconciliationOptions);

        var batch = NormalizeRequest(request);
        var summary = new PaidAccessReconciliationSummary
        {
            Offset = batch.Offset,
            Limit = batch.Limit,
            NextOffset = batch.Offset
        };
        var offset = batch.Offset;

        while (true)
        {
            var remaining = batch.EffectiveLimit - summary.Scanned;
            if (remaining <= 0)
            {
                summary.HasMore = summary.NextOffset < summary.TotalItems;
                break;
            }

            var page = await mailchimpAudienceService.ListSegmentMembersAsync(
                reconciliationOptions.PaidTagSegmentId,
                Math.Min(reconciliationOptions.BatchSize, remaining),
                offset,
                cancellationToken);
            summary.TotalItems = page.TotalItems;

            foreach (var member in page.Members)
            {
                await ReconcileMemberAsync(member, reconciliationOptions, summary, cancellationToken);
            }

            offset += page.Members.Count;
            summary.NextOffset = offset;

            if (page.Members.Count == 0 || offset >= page.TotalItems)
            {
                summary.HasMore = false;
                break;
            }
        }

        logger.LogInformation(
            "Paid access reconciliation complete. Offset: {Offset}. Limit: {Limit}. NextOffset: {NextOffset}. TotalItems: {TotalItems}. HasMore: {HasMore}. Scanned: {Scanned}. ActiveAccess: {ActiveAccess}. RemovedPaidTag: {RemovedPaidTag}. WouldRemovePaidTag: {WouldRemovePaidTag}. MissingPianoId: {MissingPianoId}. Failed: {Failed}. DryRun: {DryRun}.",
            summary.Offset,
            summary.Limit,
            summary.NextOffset,
            summary.TotalItems,
            summary.HasMore,
            summary.Scanned,
            summary.ActiveAccess,
            summary.RemovedPaidTag,
            summary.WouldRemovePaidTag,
            summary.MissingPianoId,
            summary.Failed,
            reconciliationOptions.DryRun);

        return summary;
    }

    private static (int Offset, int? Limit, int EffectiveLimit) NormalizeRequest(
        PaidAccessReconciliationRequest? request)
    {
        var offset = request?.Offset ?? 0;
        if (offset < 0)
        {
            throw new InvalidOperationException("Paid access reconciliation offset cannot be negative.");
        }

        if (request?.Limit <= 0)
        {
            throw new InvalidOperationException("Paid access reconciliation limit must be greater than zero.");
        }

        return (offset, request?.Limit, request?.Limit ?? int.MaxValue);
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

        try
        {
            var fullMember = await mailchimpAudienceService.GetMemberAsync(
                member.EmailAddress,
                cancellationToken);

            var pianoUid = fullMember.GetMergeFieldString(backfillOptions.Value.PianoIdMergeFieldName);
            if (string.IsNullOrWhiteSpace(pianoUid))
            {
                summary.MissingPianoId++;
                logger.LogWarning(
                    "Skipping paid-access check for {EmailAddress} because {PianoIdMergeFieldName} is missing.",
                    member.EmailAddress,
                    backfillOptions.Value.PianoIdMergeFieldName);
                return;
            }

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
                "Paid-access check failed for {EmailAddress}.",
                member.EmailAddress);
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
