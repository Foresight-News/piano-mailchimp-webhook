using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
        Assert.Equal("Processed", storedRecord.Status);
        Assert.Equal(eventName, storedRecord.Event);
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
        Assert.Equal("Processed", storedRecord.Status);
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

        Assert.False(interests.GetProperty("interest-daily").GetBoolean());
        Assert.True(interests.GetProperty("interest-sports").GetBoolean());

        var storedRecord = Assert.Single(await harness.ReadStoredRecordsAsync());
        Assert.Equal("Processed", storedRecord.Status);
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
        Assert.Equal("Processed", storedRecord.Status);
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
        Assert.Equal("Failed", storedRecord.Status);
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
}
