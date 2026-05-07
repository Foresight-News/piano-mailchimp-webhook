using Microsoft.Extensions.Options;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public sealed class SubscriberIdentityBackfillService(
    IMailchimpAudienceService mailchimpAudienceService,
    ISubscriberIdentityResolver subscriberIdentityResolver,
    IOptions<PaidAccessReconciliationOptions> reconciliationOptions,
    IOptions<SubscriberIdentityBackfillOptions> backfillOptions,
    ILogger<SubscriberIdentityBackfillService> logger) : ISubscriberIdentityBackfillService
{
    public async Task<SubscriberIdentityBackfillSummary> BackfillAsync(
        SubscriberIdentityBackfillRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var paidOptions = reconciliationOptions.Value;
        var identityOptions = backfillOptions.Value;
        ValidateOptions(paidOptions, identityOptions);

        var batch = NormalizeRequest(request);
        var summary = new SubscriberIdentityBackfillSummary
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
                paidOptions.PaidTagSegmentId,
                Math.Min(paidOptions.BatchSize, remaining),
                offset,
                cancellationToken);
            summary.TotalItems = page.TotalItems;

            foreach (var member in page.Members)
            {
                await BackfillMemberAsync(member, paidOptions, identityOptions, summary, cancellationToken);
            }

            offset += page.Members.Count;
            summary.NextOffset = offset;
            summary.HasMore = page.Members.Count > 0 && offset < summary.TotalItems;

            LogBatchSummary(summary, identityOptions.DryRun);

            if (page.Members.Count == 0 || offset >= page.TotalItems)
            {
                summary.HasMore = false;
                break;
            }
        }

        LogFinalSummary(summary, identityOptions.DryRun);

        return summary;
    }

    private void LogBatchSummary(SubscriberIdentityBackfillSummary summary, bool dryRun)
    {
        logger.LogInformation(
            "Subscriber identity backfill batch complete. Offset: {Offset}. Limit: {Limit}. NextOffset: {NextOffset}. TotalItems: {TotalItems}. HasMore: {HasMore}. Scanned: {Scanned}. AlreadyHadPianoId: {AlreadyHadPianoId}. Updated: {Updated}. WouldUpdate: {WouldUpdate}. NotFound: {NotFound}. Ambiguous: {Ambiguous}. Failed: {Failed}. DryRun: {DryRun}.",
            summary.Offset,
            summary.Limit,
            summary.NextOffset,
            summary.TotalItems,
            summary.HasMore,
            summary.Scanned,
            summary.AlreadyHadPianoId,
            summary.Updated,
            summary.WouldUpdate,
            summary.NotFound,
            summary.Ambiguous,
            summary.Failed,
            dryRun);
    }

    private void LogFinalSummary(SubscriberIdentityBackfillSummary summary, bool dryRun)
    {
        logger.LogInformation(
            "Subscriber identity backfill complete. Offset: {Offset}. Limit: {Limit}. NextOffset: {NextOffset}. TotalItems: {TotalItems}. HasMore: {HasMore}. Scanned: {Scanned}. AlreadyHadPianoId: {AlreadyHadPianoId}. Updated: {Updated}. WouldUpdate: {WouldUpdate}. NotFound: {NotFound}. Ambiguous: {Ambiguous}. Failed: {Failed}. DryRun: {DryRun}.",
            summary.Offset,
            summary.Limit,
            summary.NextOffset,
            summary.TotalItems,
            summary.HasMore,
            summary.Scanned,
            summary.AlreadyHadPianoId,
            summary.Updated,
            summary.WouldUpdate,
            summary.NotFound,
            summary.Ambiguous,
            summary.Failed,
            dryRun);
    }

    private static (int Offset, int? Limit, int EffectiveLimit) NormalizeRequest(
        SubscriberIdentityBackfillRequest? request)
    {
        var offset = request?.Offset ?? 0;
        if (offset < 0)
        {
            throw new InvalidOperationException("Subscriber identity backfill offset cannot be negative.");
        }

        if (request?.Limit <= 0)
        {
            throw new InvalidOperationException("Subscriber identity backfill limit must be greater than zero.");
        }

        return (offset, request?.Limit, request?.Limit ?? int.MaxValue);
    }

    private async Task BackfillMemberAsync(
        MailchimpListMember member,
        PaidAccessReconciliationOptions paidOptions,
        SubscriberIdentityBackfillOptions identityOptions,
        SubscriberIdentityBackfillSummary summary,
        CancellationToken cancellationToken)
    {
        summary.Scanned++;

        if (string.IsNullOrWhiteSpace(member.EmailAddress))
        {
            summary.Failed++;
            logger.LogWarning("Skipping Mailchimp member because email_address is missing.");
            return;
        }

        MailchimpListMember fullMember;
        try
        {
            fullMember = await mailchimpAudienceService.GetMemberAsync(
                member.EmailAddress,
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            summary.Failed++;
            logger.LogError(
                exception,
                "Subscriber identity backfill failed to load full Mailchimp member for {EmailAddress}.",
                member.EmailAddress);
            return;
        }

        var existingPianoUid = fullMember.GetMergeFieldString(identityOptions.PianoIdMergeFieldName);
        if (!string.IsNullOrWhiteSpace(existingPianoUid))
        {
            summary.AlreadyHadPianoId++;
            return;
        }

        var resolution = await subscriberIdentityResolver.ResolveAsync(member.EmailAddress, cancellationToken);

        switch (resolution.Status)
        {
            case SubscriberIdentityResolutionStatus.NotFound:
                summary.NotFound++;
                logger.LogWarning(
                    "No Piano uid mapping found for paid Mailchimp member {EmailAddress}.",
                    member.EmailAddress);
                return;

            case SubscriberIdentityResolutionStatus.Ambiguous:
                summary.Ambiguous++;
                logger.LogWarning(
                    "Ambiguous Piano uid mapping found for paid Mailchimp member {EmailAddress}.",
                    member.EmailAddress);
                return;

            case SubscriberIdentityResolutionStatus.Found:
                break;
        }

        if (string.IsNullOrWhiteSpace(resolution.PianoUid))
        {
            summary.Failed++;
            logger.LogWarning(
                "Resolver returned a found result without a Piano uid for {EmailAddress}.",
                member.EmailAddress);
            return;
        }

        if (identityOptions.DryRun)
        {
            summary.WouldUpdate++;
            logger.LogInformation(
                "Dry run: would set {PianoIdMergeFieldName} for {EmailAddress} to Piano uid {PianoUid}.",
                identityOptions.PianoIdMergeFieldName,
                member.EmailAddress,
                resolution.PianoUid);
            return;
        }

        try
        {
            await mailchimpAudienceService.UpdateMemberMergeFieldsAsync(
                member.EmailAddress,
                new Dictionary<string, object?>
                {
                    [identityOptions.PianoIdMergeFieldName] = resolution.PianoUid
                },
                cancellationToken);
            summary.Updated++;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            summary.Failed++;
            logger.LogError(
                exception,
                "Subscriber identity backfill failed for {EmailAddress}.",
                member.EmailAddress);
        }
    }

    private static void ValidateOptions(
        PaidAccessReconciliationOptions paidOptions,
        SubscriberIdentityBackfillOptions identityOptions)
    {
        if (string.IsNullOrWhiteSpace(paidOptions.PaidTagSegmentId))
        {
            throw new InvalidOperationException(
                "PaidAccessReconciliation:PaidTagSegmentId is required.");
        }

        if (paidOptions.BatchSize <= 0)
        {
            throw new InvalidOperationException(
                "PaidAccessReconciliation:BatchSize must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(identityOptions.PianoIdMergeFieldName))
        {
            throw new InvalidOperationException(
                "SubscriberIdentityBackfill:PianoIdMergeFieldName is required.");
        }
    }
}
