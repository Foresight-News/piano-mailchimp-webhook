using Microsoft.Extensions.Options;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public sealed class PianoWebhookProcessor(
    IPianoApiClient pianoApiClient,
    IMailchimpAudienceService mailchimpAudienceService,
    INewsletterPreferenceMapper newsletterPreferenceMapper,
    IOptions<PianoOptions> pianoOptions,
    ILogger<PianoWebhookProcessor> logger) : IPianoWebhookProcessor
{
    private static readonly HashSet<string> SupportedEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "user_created",
        "user_updated",
        "piano_id_user_custom_fields_updated"
    };

    public async Task ProcessAsync(PianoWebhookEvent webhookEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(webhookEvent);

        var eventName = webhookEvent.Event?.Trim();
        if (string.IsNullOrWhiteSpace(eventName))
        {
            logger.LogWarning("Skipping Piano webhook because the event value is missing.");
            return;
        }

        if (!SupportedEvents.Contains(eventName))
        {
            logger.LogInformation(
                "Skipping Piano webhook event {EventName} because it is not handled by this processor.",
                eventName);
            return;
        }

        var uid = webhookEvent.Uid?.Trim();
        if (string.IsNullOrWhiteSpace(uid))
        {
            logger.LogWarning(
                "Skipping Piano webhook event {EventName} because the uid is missing.",
                eventName);
            return;
        }

        logger.LogInformation(
            "Processing Piano webhook event {EventName} for uid {Uid}.",
            eventName,
            uid);

        var user = await pianoApiClient.GetUserAsync(uid, cancellationToken);
        if (user is null)
        {
            logger.LogWarning(
                "Skipping Piano webhook event {EventName} for uid {Uid} because no Piano user profile was returned.",
                eventName,
                uid);
            return;
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            logger.LogInformation(
                "Skipping Piano webhook event {EventName} for uid {Uid} because the user has no email address.",
                eventName,
                uid);
            return;
        }

        var request = new MailchimpMemberUpsertRequest
        {
            EmailAddress = user.Email.Trim(),
            // Keep the subscription rule isolated here so consent behavior is easy to change later.
            StatusIfNew = "subscribed",
            MergeFields = new Dictionary<string, object?>
            {
                ["FNAME"] = user.FirstName,
                ["LNAME"] = user.LastName,
                ["PIANOID"] = user.Uid ?? uid
            },
            Interests = newsletterPreferenceMapper.BuildInterestMap(user)
        };

        await mailchimpAudienceService.UpsertMemberAsync(request, cancellationToken);
        var hasPaidAccess = await pianoApiClient.HasActiveAccessToAnyResourceAsync(
            uid,
            pianoOptions.Value.PaidResourceIds,
            cancellationToken);

        if (hasPaidAccess)
        {
            await mailchimpAudienceService.AddMemberTagsAsync(request.EmailAddress, ["PAID"], cancellationToken);
        }

        logger.LogInformation(
            "Upserted Mailchimp audience member for Piano uid {Uid} and email {EmailAddress}.",
            uid,
            request.EmailAddress);
    }
}
