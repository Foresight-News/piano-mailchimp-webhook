using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public sealed class PianoApiClient(
    HttpClient httpClient,
    IOptions<PianoOptions> options,
    ILogger<PianoApiClient> logger) : IPianoApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PianoUserProfile?> GetUserAsync(string uid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uid))
        {
            throw new ArgumentException("UID is required.", nameof(uid));
        }

        var pianoOptions = options.Value;
        ConfigureClient(pianoOptions);

        var requestUri = BuildGetUserRequestUri(uid, pianoOptions);
        using var response = await httpClient.GetAsync(requestUri, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogWarning(
                "Piano user lookup returned 404 for uid {Uid}. RequestUri: {RequestUri}",
                uid,
                requestUri);

            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

            logger.LogError(
                "Piano user lookup failed for uid {Uid}. Status: {StatusCode}. RequestUri: {RequestUri}. Response: {ResponseBody}",
                uid,
                (int)response.StatusCode,
                requestUri,
                errorBody);

            throw new HttpRequestException(
                $"Piano user lookup failed with status code {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var userProfile = DeserializeUserProfile(responseBody);

        if (userProfile is not null)
        {
            return userProfile;
        }

        logger.LogError(
            "Piano user lookup returned a successful response but could not be deserialized for uid {Uid}. RequestUri: {RequestUri}. Response: {ResponseBody}",
            uid,
            requestUri,
            responseBody);

        throw new JsonException("Piano user response could not be deserialized.");
    }

    private void ConfigureClient(PianoOptions pianoOptions)
    {
        if (string.IsNullOrWhiteSpace(pianoOptions.BaseUrl))
        {
            throw new InvalidOperationException("Piano BaseUrl is not configured.");
        }

        if (string.IsNullOrWhiteSpace(pianoOptions.ApiToken))
        {
            throw new InvalidOperationException("Piano ApiToken is not configured.");
        }

        if (string.IsNullOrWhiteSpace(pianoOptions.ApplicationId))
        {
            throw new InvalidOperationException("Piano ApplicationId is not configured.");
        }

        httpClient.BaseAddress = new Uri($"{pianoOptions.BaseUrl.TrimEnd('/')}/", UriKind.Absolute);
    }

    // Keep the route isolated here so it is easy to swap once the final Piano endpoint is confirmed.
    private static string BuildGetUserRequestUri(string uid, PianoOptions pianoOptions)
    {
        var query = QueryString.Create(new Dictionary<string, string?>
        {
            ["aid"] = pianoOptions.ApplicationId,
            ["api_token"] = pianoOptions.ApiToken,
            ["uid"] = uid
        });

        return $"api/v3/publisher/user/get{query}";
    }

    private static PianoUserProfile? DeserializeUserProfile(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("user", out var userElement) &&
            userElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return userElement.Deserialize<PianoUserProfile>(JsonOptions);
        }

        return document.RootElement.Deserialize<PianoUserProfile>(JsonOptions);
    }
}
