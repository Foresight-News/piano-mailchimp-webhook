using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public sealed class PianoSubscriberIdentityResolver(
    IPianoApiClient pianoApiClient,
    ILogger<PianoSubscriberIdentityResolver> logger) : ISubscriberIdentityResolver
{
    public async Task<SubscriberIdentityResolution> ResolveAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return SubscriberIdentityResolution.NotFound;
        }

        var normalizedEmail = NormalizeEmail(email);
        var users = await pianoApiClient.SearchUsersByEmailAsync(normalizedEmail, cancellationToken);
        var matchingUids = users
            .Where(user => string.Equals(NormalizeEmail(user.Email), normalizedEmail, StringComparison.Ordinal))
            .Select(user => user.Uid?.Trim())
            .Where(uid => !string.IsNullOrWhiteSpace(uid))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return matchingUids.Count switch
        {
            0 => SubscriberIdentityResolution.NotFound,
            1 => SubscriberIdentityResolution.Found(matchingUids[0]!),
            _ => LogAmbiguous(normalizedEmail, matchingUids.Count)
        };
    }

    private SubscriberIdentityResolution LogAmbiguous(string email, int matchCount)
    {
        logger.LogWarning(
            "Piano email lookup returned {MatchCount} distinct uids for {EmailAddress}.",
            matchCount,
            email);

        return SubscriberIdentityResolution.Ambiguous;
    }

    private static string NormalizeEmail(string? email) => email?.Trim().ToLowerInvariant() ?? string.Empty;
}
