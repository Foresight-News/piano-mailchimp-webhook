using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public interface IMailchimpAudienceService
{
    Task UpsertMemberAsync(
        MailchimpMemberUpsertRequest request,
        CancellationToken cancellationToken = default);

    Task AddMemberTagsAsync(
        string email,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default);
}
