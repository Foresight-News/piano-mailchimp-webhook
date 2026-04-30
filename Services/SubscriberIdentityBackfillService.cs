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
        CancellationToken cancellationToken = default)
    {
        var paidOptions = reconciliationOptions.Value;
        var identityOptions = backfillOptions.Value;
        ValidateOptions(paidOptions, identityOptions);

        var summary = new SubscriberIdentityBackfillSummary();
        var offset = 0;

        while (true)
        {
            var page = await mailchimpAudienceService.ListSegmentMembersAsync(
                paidOptions.PaidTagSegmentId,
                paidOptions.BatchSize,
                offset,
                cancellationToken);

            foreach (var member in page.Members)
            {
                await BackfillMemberAsync(member, paidOptions, identityOptions, summary, cancellationToken);
            }

            offset += page.Members.Count;

            if (page.Members.Count == 0 || offset >= page.TotalItems)
            {
                break;
            }
        }

        logger.LogInformation(
            "Subscriber identity backfill complete. Scanned: {Scanned}. AlreadyHadPianoId: {AlreadyHadPianoId}. Updated: {Updated}. WouldUpdate: {WouldUpdate}. NotFound: {NotFound}. Ambiguous: {Ambiguous}. Failed: {Failed}. DryRun: {DryRun}.",
            summary.Scanned,
            summary.AlreadyHadPianoId,
            summary.Updated,
            summary.WouldUpdate,
            summary.NotFound,
            summary.Ambiguous,
            summary.Failed,
            identityOptions.DryRun);

        return summary;
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

        var existingPianoUid = member.GetMergeFieldString(identityOptions.PianoIdMergeFieldName);
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
