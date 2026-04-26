using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Models;
using piano_mailchimp_webhook.Services;
using Xunit;

namespace piano_mailchimp_webhook.IntegrationTests;

public sealed class PianoWebhookFlowIntegrationTests
{
    [Theory]
    [InlineData("user_created")]
    [InlineData("user_updated")]
    public async Task SupportedUserEventsFetchFromPianoAndUpsertMailchimp(string eventName)
    {
        await using var harness = new WebhookFlowHarness(
            new PianoUserProfile
            {
                Uid = "user-123",
                Email = "ada@example.com",
                FirstName = "Ada",
                LastName = "Lovelace",
                CustomFields = new Dictionary<string, object?>
                {
                    ["daily_news"] = true,
                    ["sports_news"] = "0"
                }
            });

        var result = await harness.SendWebhookAsync(CreateWebhookEvent(eventName));

        Assert.IsType<OkObjectResult>(result);

        var pianoRequest = Assert.Single(harness.PianoRequests);
        Assert.Equal(HttpMethod.Get, pianoRequest.Method);
        Assert.Equal("/api/v3/publisher/user/get", pianoRequest.RequestUri.AbsolutePath);
        Assert.Contains("uid=user-123", pianoRequest.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("aid=test-application", pianoRequest.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("api_token=test-piano-token", pianoRequest.RequestUri.Query, StringComparison.Ordinal);

        var mailchimpRequest = Assert.Single(harness.MailchimpRequests);
        Assert.Equal(HttpMethod.Put, mailchimpRequest.Method);
        Assert.Equal(
            $"/3.0/lists/test-audience/members/{SubscriberHash.FromEmail("ada@example.com")}",
            mailchimpRequest.RequestUri.AbsolutePath);

        using var requestBody = JsonDocument.Parse(mailchimpRequest.Body!);
        var root = requestBody.RootElement;

        Assert.Equal("ada@example.com", root.GetProperty("email_address").GetString());
        Assert.Equal("subscribed", root.GetProperty("status_if_new").GetString());
        Assert.Equal("Ada", root.GetProperty("merge_fields").GetProperty("FNAME").GetString());
        Assert.Equal("Lovelace", root.GetProperty("merge_fields").GetProperty("LNAME").GetString());
        Assert.Equal("user-123", root.GetProperty("merge_fields").GetProperty("PIANOID").GetString());
        Assert.True(root.GetProperty("interests").GetProperty("interest-daily").GetBoolean());
        Assert.False(root.GetProperty("interests").GetProperty("interest-sports").GetBoolean());

        var storedRecord = Assert.Single(await harness.ReadStoredRecordsAsync());
        Assert.Equal(PianoWebhookEventStatuses.Processed, storedRecord.Status);
        Assert.Equal(eventName, storedRecord.Event);
        Assert.Equal("user-123", storedRecord.Uid);
    }

    [Fact]
    public async Task SamplePianoPayloadRefreshesManagedCustomField()
    {
        await using var harness = new WebhookFlowHarness(
            new PianoUserProfile
            {
                Uid = "PNIP9h8uNt6ldu6",
                Email = "john.tangen@foresightnews.com",
                FirstName = "John",
                LastName = "Tangen",
                CustomFields = new Dictionary<string, object?>
                {
                    ["FN03"] = true
                }
            },
            fieldMappings:
            [
                new NewsletterFieldMapping
                {
                    PianoFieldName = "FN03",
                    MailchimpInterestId = "interest-fn03"
                }
            ]);

        var samplePayload = await File.ReadAllTextAsync(GetRepositoryFilePath("webhook-payload-sample.json"));

        var result = await harness.SendRawWebhookAsync(samplePayload, "application/json");

        Assert.IsType<OkObjectResult>(result);

        var pianoRequest = Assert.Single(harness.PianoRequests);
        Assert.Contains("uid=PNIP9h8uNt6ldu6", pianoRequest.RequestUri.Query, StringComparison.Ordinal);

        var mailchimpRequest = Assert.Single(harness.MailchimpRequests);
        using var requestBody = JsonDocument.Parse(mailchimpRequest.Body!);
        var root = requestBody.RootElement;

        Assert.Equal("john.tangen@foresightnews.com", root.GetProperty("email_address").GetString());
        Assert.True(root.GetProperty("interests").GetProperty("interest-fn03").GetBoolean());

        var storedRecord = Assert.Single(await harness.ReadStoredRecordsAsync());
        Assert.Equal(PianoWebhookEventStatuses.Processed, storedRecord.Status);
        Assert.Equal("piano_id_user_custom_fields_updated", storedRecord.Event);
        Assert.Equal("PNIP9h8uNt6ldu6", storedRecord.Uid);
    }

    [Fact]
    public async Task EnvelopedPianoUserResponseIsUsedForMailchimpSync()
    {
        var pianoResponseBody = """
            {
              "user": {
                "uid": "user-123",
                "email": "ada@example.com",
                "first_name": "Ada",
                "last_name": "Lovelace",
                "custom_fields": {
                  "daily_news": true
                }
              }
            }
            """;

        await using var harness = new WebhookFlowHarness(
            new PianoUserProfile { Uid = "user-123" },
            pianoResponseBody: pianoResponseBody);

        var result = await harness.SendWebhookAsync(CreateWebhookEvent("user_updated"));

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(harness.PianoRequests);

        var mailchimpRequest = Assert.Single(harness.MailchimpRequests);
        using var requestBody = JsonDocument.Parse(mailchimpRequest.Body!);
        var root = requestBody.RootElement;

        Assert.Equal("ada@example.com", root.GetProperty("email_address").GetString());
        Assert.Equal("Ada", root.GetProperty("merge_fields").GetProperty("FNAME").GetString());
        Assert.True(root.GetProperty("interests").GetProperty("interest-daily").GetBoolean());
    }

    [Fact]
    public async Task PianoUserCustomFieldsArrayIsUsedForMailchimpInterests()
    {
        var pianoResponseBody = """
            {
              "user": {
                "uid": "user-123",
                "email": "ada@example.com",
                "first_name": "Ada",
                "last_name": "Lovelace",
                "custom_fields": [
                  {
                    "field_name": "daily_news",
                    "value": true
                  },
                  {
                    "field_name": "sports_news",
                    "value": "0"
                  }
                ]
              }
            }
            """;

        await using var harness = new WebhookFlowHarness(
            new PianoUserProfile { Uid = "user-123" },
            pianoResponseBody: pianoResponseBody);

        var result = await harness.SendWebhookAsync(CreateWebhookEvent("user_updated"));

        Assert.IsType<OkObjectResult>(result);

        var mailchimpRequest = Assert.Single(harness.MailchimpRequests);
        using var requestBody = JsonDocument.Parse(mailchimpRequest.Body!);
        var interests = requestBody.RootElement.GetProperty("interests");

        Assert.True(interests.GetProperty("interest-daily").GetBoolean());
        Assert.False(interests.GetProperty("interest-sports").GetBoolean());
    }

    [Fact]
    public async Task FormUrlEncodedWebhookPayloadIsAccepted()
    {
        await using var harness = new WebhookFlowHarness(
            new PianoUserProfile
            {
                Uid = "user-123",
                Email = "ada@example.com",
                FirstName = "Ada",
                LastName = "Lovelace",
                CustomFields = new Dictionary<string, object?>
                {
                    ["daily_news"] = true,
                    ["sports_news"] = false
                }
            });

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["event"] = "user_updated",
            ["uid"] = "user-123",
            ["aid"] = "aid-123",
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        });

        var result = await harness.SendRawWebhookAsync(
            await content.ReadAsStringAsync(),
            "application/x-www-form-urlencoded");

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(harness.PianoRequests);
        Assert.Single(harness.MailchimpRequests);

        var storedRecord = Assert.Single(await harness.ReadStoredRecordsAsync());
        Assert.Equal(PianoWebhookEventStatuses.Processed, storedRecord.Status);
        Assert.Equal("user_updated", storedRecord.Event);
        Assert.Equal("user-123", storedRecord.Uid);
    }

    [Fact]
    public async Task GetWebhookValidationRequestReturnsOkWithoutProcessing()
    {
        await using var harness = new WebhookFlowHarness(
            new PianoUserProfile
            {
                Uid = "user-123",
                Email = "ada@example.com"
            });

        var result = await harness.SendGetWebhookAsync();

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(harness.PianoRequests);
        Assert.Empty(harness.MailchimpRequests);
        Assert.Empty(await harness.ReadStoredRecordsAsync());
    }

    [Fact]
    public async Task EncryptedGetWebhookPayloadIsDecryptedAndProcessed()
    {
        await using var harness = new WebhookFlowHarness(
            new PianoUserProfile
            {
                Uid = "user-123",
                Email = "ada@example.com",
                FirstName = "Ada",
                LastName = "Lovelace",
                CustomFields = new Dictionary<string, object?>
                {
                    ["daily_news"] = false,
                    ["sports_news"] = true
                }
            });

        var encryptedData = EncryptPianoWebhookData(
            "test-private-key",
            """
            {
              "type": "user_updated",
              "aid": "aid-123",
              "uid": "user-123",
              "timestamp": "1777133250",
              "user_email": "ada@example.com",
              "updated_custom_fields": "sports_news"
            }
            """);

        var result = await harness.SendGetWebhookAsync(encryptedData);

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(harness.PianoRequests);
        var mailchimpRequest = Assert.Single(harness.MailchimpRequests);
        using var requestBody = JsonDocument.Parse(mailchimpRequest.Body!);
        var interests = requestBody.RootElement.GetProperty("interests");

        Assert.False(interests.TryGetProperty("interest-daily", out _));
        Assert.True(interests.GetProperty("interest-sports").GetBoolean());

        var storedRecord = Assert.Single(await harness.ReadStoredRecordsAsync());
        Assert.Equal(PianoWebhookEventStatuses.Processed, storedRecord.Status);
        Assert.Equal("user_updated", storedRecord.Event);
        Assert.Equal("user-123", storedRecord.Uid);
    }

    [Fact]
    public async Task CustomFieldUpdateIsIgnoredWhenNoManagedFieldsChanged()
    {
        await using var harness = new WebhookFlowHarness(
            new PianoUserProfile
            {
                Uid = "user-123",
                Email = "ada@example.com"
            });

        var result = await harness.SendWebhookAsync(
            CreateWebhookEvent(
                "piano_id_user_custom_fields_updated",
                updatedCustomFields: "profile_color, favorite_topic"));

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(harness.PianoRequests);
        Assert.Empty(harness.MailchimpRequests);
        Assert.Contains(
            harness.Logs,
            entry => entry.Level == LogLevel.Information &&
                     entry.Message.Contains(
                         "none of the changed fields are newsletter-managed",
                         StringComparison.Ordinal));

        var storedRecord = Assert.Single(await harness.ReadStoredRecordsAsync());
        Assert.Equal(PianoWebhookEventStatuses.Processed, storedRecord.Status);
    }

    [Fact]
    public async Task ManagedCustomFieldUpdateRefreshesMailchimpInterests()
    {
        await using var harness = new WebhookFlowHarness(
            new PianoUserProfile
            {
                Uid = "user-123",
                Email = "ada@example.com",
                FirstName = "Ada",
                LastName = "Lovelace",
                CustomFields = new Dictionary<string, object?>
                {
                    ["daily_news"] = 0,
                    ["sports_news"] = "yes"
                }
            });

        var result = await harness.SendWebhookAsync(
            CreateWebhookEvent(
                "piano_id_user_custom_fields_updated",
                updatedCustomFields: "profile_color, sports_news"));

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(harness.PianoRequests);

        var mailchimpRequest = Assert.Single(harness.MailchimpRequests);
        using var requestBody = JsonDocument.Parse(mailchimpRequest.Body!);
        var interests = requestBody.RootElement.GetProperty("interests");

        Assert.False(interests.TryGetProperty("interest-daily", out _));
        Assert.True(interests.GetProperty("interest-sports").GetBoolean());

        var storedRecord = Assert.Single(await harness.ReadStoredRecordsAsync());
        Assert.Equal(PianoWebhookEventStatuses.Processed, storedRecord.Status);
    }

    [Fact]
    public async Task UserUpdatedWithUpdatedCustomFieldsOnlyUpdatesThoseInterests()
    {
        await using var harness = new WebhookFlowHarness(
            new PianoUserProfile
            {
                Uid = "user-123",
                Email = "ada@example.com",
                FirstName = "Ada",
                LastName = "Lovelace",
                CustomFields = new Dictionary<string, object?>
                {
                    ["daily_news"] = false,
                    ["sports_news"] = true
                }
            });

        var result = await harness.SendWebhookAsync(
            CreateWebhookEvent(
                "user_updated",
                updatedCustomFields: "sports_news"));

        Assert.IsType<OkObjectResult>(result);

        var mailchimpRequest = Assert.Single(harness.MailchimpRequests);
        using var requestBody = JsonDocument.Parse(mailchimpRequest.Body!);
        var interests = requestBody.RootElement.GetProperty("interests");

        Assert.False(interests.TryGetProperty("interest-daily", out _));
        Assert.True(interests.GetProperty("interest-sports").GetBoolean());
    }

    [Fact]
    public async Task MissingEmailSkipsMailchimpSync()
    {
        await using var harness = new WebhookFlowHarness(
            new PianoUserProfile
            {
                Uid = "user-123",
                Email = "   ",
                FirstName = "Ada",
                LastName = "Lovelace"
            });

        var result = await harness.SendWebhookAsync(CreateWebhookEvent("user_updated"));

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(harness.PianoRequests);
        Assert.Empty(harness.MailchimpRequests);
        Assert.Contains(
            harness.Logs,
            entry => entry.Level == LogLevel.Information &&
                     entry.Message.Contains("has no email address", StringComparison.Ordinal));

        var storedRecord = Assert.Single(await harness.ReadStoredRecordsAsync());
        Assert.Equal(PianoWebhookEventStatuses.Processed, storedRecord.Status);
    }

    [Fact]
    public async Task MailchimpFailureIsLoggedAndSurfaced()
    {
        await using var harness = new WebhookFlowHarness(
            new PianoUserProfile
            {
                Uid = "user-123",
                Email = "ada@example.com",
                FirstName = "Ada",
                LastName = "Lovelace",
                CustomFields = new Dictionary<string, object?>
                {
                    ["daily_news"] = true,
                    ["sports_news"] = false
                }
            },
            mailchimpStatusCode: HttpStatusCode.BadRequest,
            mailchimpResponseBody: "{\"detail\":\"Invalid resource\"}");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => harness.SendWebhookAsync(CreateWebhookEvent("user_created")));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("status code 400", exception.Message, StringComparison.Ordinal);
        Assert.Single(harness.PianoRequests);
        Assert.Single(harness.MailchimpRequests);
        Assert.Contains(
            harness.Logs,
            entry => entry.Level == LogLevel.Error &&
                     entry.Message.Contains(
                         "Mailchimp member upsert failed for ada@example.com. Status: 400. Response: {\"detail\":\"Invalid resource\"}",
                         StringComparison.Ordinal));
        Assert.Contains(
            harness.Logs,
            entry => entry.Level == LogLevel.Error &&
                     entry.Message.Contains(
                         "Processing failed for Piano webhook event user_created and uid user-123.",
                         StringComparison.Ordinal));

        var storedRecord = Assert.Single(await harness.ReadStoredRecordsAsync());
        Assert.Equal(PianoWebhookEventStatuses.Failed, storedRecord.Status);
        Assert.Contains("status code 400", storedRecord.ErrorMessage, StringComparison.Ordinal);
    }

    private static PianoWebhookEvent CreateWebhookEvent(
        string eventName,
        string uid = "user-123",
        string? updatedCustomFields = null)
    {
        return new PianoWebhookEvent
        {
            Event = eventName,
            Uid = uid,
            Aid = "aid-123",
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            UpdatedCustomFields = updatedCustomFields
        };
    }

    private static string GetRepositoryFilePath(string fileName)
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            fileName));
    }

    private static string EncryptPianoWebhookData(string privateKey, string payload)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var paddingLength = (16 - payloadBytes.Length % 16) % 16;
        if (paddingLength > 0)
        {
            var paddedBytes = new byte[payloadBytes.Length + paddingLength];
            payloadBytes.CopyTo(paddedBytes, 0);
            Array.Fill(paddedBytes, (byte)paddingLength, payloadBytes.Length, paddingLength);
            payloadBytes = paddedBytes;
        }

        using var aes = Aes.Create();
        aes.Key = BuildCipherKey(privateKey);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        using var encryptor = aes.CreateEncryptor();
        var encryptedBytes = encryptor.TransformFinalBlock(payloadBytes, 0, payloadBytes.Length);
        var encryptedPayload = Base64UrlEncode(encryptedBytes);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(privateKey));
        var signature = Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(encryptedPayload)));

        return $"{encryptedPayload}~~~{signature}";
    }

    private static byte[] BuildCipherKey(string privateKey)
    {
        var cipherKey = privateKey.Length > 32
            ? privateKey[..32]
            : privateKey.PadRight(32, 'X');

        return Encoding.UTF8.GetBytes(cipherKey);
    }

    private static string Base64UrlEncode(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
