using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public interface IPianoApiClient
{
    Task<PianoUserProfile?> GetUserAsync(string uid, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PianoUserProfile>> SearchUsersByEmailAsync(
        string email,
        CancellationToken cancellationToken = default);

    Task<bool> HasActiveAccessToAnyResourceAsync(
        string uid,
        CancellationToken cancellationToken = default);
}
