using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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

    public async Task<bool> HasMemberTagAsync(
        string email,
        string tag,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email address is required.", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new ArgumentException("Tag is required.", nameof(tag));
        }

        var mailchimpOptions = options.Value;
        var subscriberHash = SubscriberHash.FromEmail(email);

        ConfigureClient(mailchimpOptions);

        var requestUri = $"lists/{mailchimpOptions.AudienceId}/members/{subscriberHash}/tags";
        using var response = await httpClient.GetAsync(requestUri, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

            logger.LogError(
                "Mailchimp member tags lookup failed for {EmailAddress}. Status: {StatusCode}. Response: {ResponseBody}",
                email,
                (int)response.StatusCode,
                errorBody);

            throw new HttpRequestException(
                $"Mailchimp member tags lookup failed with status code {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return MemberTagsContain(responseBody, tag);
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

    private static bool MemberTagsContain(string responseBody, string tag)
    {
        using var document = JsonDocument.Parse(responseBody);

        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("tags", out var tags) ||
            tags.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var tagElement in tags.EnumerateArray())
        {
            if (tagElement.ValueKind != JsonValueKind.Object ||
                !tagElement.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (string.Equals(name.GetString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
