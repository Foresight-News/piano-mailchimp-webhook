using Microsoft.Extensions.Options;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public sealed class ConfiguredSubscriberIdentityResolver(
    CsvSubscriberIdentityResolver csvResolver,
    PianoSubscriberIdentityResolver pianoResolver,
    IOptions<SubscriberIdentityBackfillOptions> options) : ISubscriberIdentityResolver
{
    public Task<SubscriberIdentityResolution> ResolveAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        return options.Value.ResolverSource.Trim().ToLowerInvariant() switch
        {
            "piano" => pianoResolver.ResolveAsync(email, cancellationToken),
            "csv" => csvResolver.ResolveAsync(email, cancellationToken),
            _ => throw new InvalidOperationException(
                "SubscriberIdentityBackfill:ResolverSource must be either Csv or Piano.")
        };
    }
}
