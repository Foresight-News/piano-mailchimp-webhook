using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public interface IMailchimpAudienceService
{
    Task<MailchimpListMembersPage> ListSegmentMembersAsync(
        string segmentId,
        int count,
        int offset,
        CancellationToken cancellationToken = default);

    Task UpsertMemberAsync(
        MailchimpMemberUpsertRequest request,
        CancellationToken cancellationToken = default);

    Task UpdateMemberMergeFieldsAsync(
        string email,
        IReadOnlyDictionary<string, object?> mergeFields,
        CancellationToken cancellationToken = default);

    Task AddMemberTagsAsync(
        string email,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default);

    Task RemoveMemberTagsAsync(
        string email,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default);
}
