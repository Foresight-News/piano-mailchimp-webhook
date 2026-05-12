using System.Text.Json;
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

    public async Task<bool> HasActiveAccessByEmailAsync(
        string emailAddress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
        {
            throw new ArgumentException("Email address is required.", nameof(emailAddress));
        }

        var normalizedEmailAddress = emailAddress.Trim();
        var pianoOptions = options.Value;
        ConfigureClient(pianoOptions);

        var requestUri = BuildActiveUserSearchRequestUri(normalizedEmailAddress, pianoOptions);
        using var response = await httpClient.GetAsync(requestUri, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

            logger.LogError(
                "Piano active user search failed for email {EmailAddress}. Status: {StatusCode}. RequestUri: {RequestUri}. Response: {ResponseBody}",
                normalizedEmailAddress,
                (int)response.StatusCode,
                requestUri,
                errorBody);

            throw new HttpRequestException(
                $"Piano active user search failed with status code {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return SearchResponseContainsEmail(responseBody, normalizedEmailAddress);
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

        httpClient.BaseAddress ??= new Uri($"{pianoOptions.BaseUrl.TrimEnd('/')}/", UriKind.Absolute);
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

    private static string BuildActiveUserSearchRequestUri(string emailAddress, PianoOptions pianoOptions)
    {
        var query = QueryString.Create(new Dictionary<string, string?>
        {
            ["source"] = pianoOptions.SearchSource,
            ["limit"] = "10",
            ["offset"] = "0",
            ["order_direction"] = "asc",
            ["has_access"] = "true",
            ["aid"] = pianoOptions.ApplicationId,
            ["api_token"] = pianoOptions.ApiToken,
            ["email"] = emailAddress
        });

        return $"api/v3/publisher/user/search{query}";
    }

    private static PianoUserProfile? DeserializeUserProfile(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("user", out var userElement) &&
            userElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return DeserializeUserProfileElement(userElement);
        }

        return DeserializeUserProfileElement(document.RootElement);
    }

    private static PianoUserProfile? DeserializeUserProfileElement(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return new PianoUserProfile
        {
            Uid = GetStringProperty(element, "uid"),
            Email = GetStringProperty(element, "email"),
            FirstName = GetStringProperty(element, "first_name"),
            LastName = GetStringProperty(element, "last_name"),
            CustomFields = GetCustomFields(element)
        };
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
    }

    private static Dictionary<string, object?> GetCustomFields(JsonElement element)
    {
        if (!element.TryGetProperty("custom_fields", out var customFields) ||
            customFields.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        return customFields.ValueKind switch
        {
            JsonValueKind.Object => ParseCustomFieldsObject(customFields),
            JsonValueKind.Array => ParseCustomFieldsArray(customFields),
            _ => []
        };
    }

    private static Dictionary<string, object?> ParseCustomFieldsObject(JsonElement customFields)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in customFields.EnumerateObject())
        {
            values[property.Name] = ConvertJsonValue(property.Value);
        }

        return values;
    }

    private static Dictionary<string, object?> ParseCustomFieldsArray(JsonElement customFields)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in customFields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var fieldName =
                GetStringProperty(field, "field_name") ??
                GetStringProperty(field, "fieldName") ??
                GetStringProperty(field, "field") ??
                GetStringProperty(field, "field_id") ??
                GetStringProperty(field, "fieldId") ??
                GetStringProperty(field, "id") ??
                GetStringProperty(field, "name") ??
                GetStringProperty(field, "key");

            if (string.IsNullOrWhiteSpace(fieldName))
            {
                continue;
            }

            if (TryGetProperty(
                    field,
                    out var value,
                    "value",
                    "field_value",
                    "fieldValue",
                    "values",
                    "val"))
            {
                values[fieldName] = ConvertJsonValue(value);
            }
        }

        return values;
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static object? ConvertJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue,
            _ => value.Clone()
        };
    }

    private static bool SearchResponseContainsEmail(string responseBody, string emailAddress)
    {
        using var document = JsonDocument.Parse(responseBody);

        return ExtractUsers(document.RootElement)
            .Select(user => GetStringProperty(user, "email")?.Trim())
            .Any(email => string.Equals(email, emailAddress, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<JsonElement> ExtractUsers(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        foreach (var propertyName in new[] { "users", "data", "items", "results" })
        {
            if (root.TryGetProperty(propertyName, out var users) &&
                users.ValueKind == JsonValueKind.Array)
            {
                return users.EnumerateArray();
            }
        }

        if (root.TryGetProperty("result", out var nested) &&
            nested.ValueKind == JsonValueKind.Object)
        {
            return ExtractUsers(nested);
        }

        return [];
    }
}
