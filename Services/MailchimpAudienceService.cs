using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Options;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public sealed class MailchimpAudienceService(
    HttpClient httpClient,
    IOptions<MailchimpOptions> options,
    ILogger<MailchimpAudienceService> logger) : IMailchimpAudienceService
{
    public async Task UpsertMemberAsync(
        MailchimpMemberUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var mailchimpOptions = options.Value;
        var subscriberHash = SubscriberHash.FromEmail(request.EmailAddress);

        ConfigureClient(mailchimpOptions);

        var requestUri = $"lists/{mailchimpOptions.AudienceId}/members/{subscriberHash}";
        using var response = await httpClient.PutAsJsonAsync(requestUri, request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

        logger.LogError(
            "Mailchimp member upsert failed for {EmailAddress}. Status: {StatusCode}. Response: {ResponseBody}",
            request.EmailAddress,
            (int)response.StatusCode,
            errorBody);

        throw new HttpRequestException(
            $"Mailchimp member upsert failed with status code {(int)response.StatusCode}.",
            null,
            response.StatusCode);
    }

    public async Task AddMemberTagsAsync(
        string email,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
    {
        await UpdateMemberTagsAsync(email, tags, "active", cancellationToken);
    }

    public async Task RemoveMemberTagsAsync(
        string email,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
    {
        await UpdateMemberTagsAsync(email, tags, "inactive", cancellationToken);
    }

    private async Task UpdateMemberTagsAsync(
        string email,
        IEnumerable<string> tags,
        string status,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email address is required.", nameof(email));
        }

        var mailchimpOptions = options.Value;
        var subscriberHash = SubscriberHash.FromEmail(email);

        ConfigureClient(mailchimpOptions);

        var requestUri = $"lists/{mailchimpOptions.AudienceId}/members/{subscriberHash}/tags";
        var body = new
        {
            tags = tags.Select(t => new { name = t, status }).ToList()
        };

        using var response = await httpClient.PostAsJsonAsync(requestUri, body, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

        logger.LogError(
            "Mailchimp member tag update failed for {EmailAddress}. Status: {StatusCode}. Response: {ResponseBody}",
            email,
            (int)response.StatusCode,
            errorBody);

        throw new HttpRequestException(
            $"Mailchimp member tag update failed with status code {(int)response.StatusCode}.",
            null,
            response.StatusCode);
    }

    private void ConfigureClient(MailchimpOptions mailchimpOptions)
    {
        if (string.IsNullOrWhiteSpace(mailchimpOptions.ServerPrefix))
        {
            throw new InvalidOperationException("Mailchimp ServerPrefix is not configured.");
        }

        if (string.IsNullOrWhiteSpace(mailchimpOptions.ApiKey))
        {
            throw new InvalidOperationException("Mailchimp ApiKey is not configured.");
        }

        if (string.IsNullOrWhiteSpace(mailchimpOptions.AudienceId))
        {
            throw new InvalidOperationException("Mailchimp AudienceId is not configured.");
        }

        httpClient.BaseAddress ??= new Uri(
            $"https://{mailchimpOptions.ServerPrefix}.api.mailchimp.com/3.0/",
            UriKind.Absolute);

        var credentials = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"anystring:{mailchimpOptions.ApiKey}"));

        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", credentials);
    }
}
