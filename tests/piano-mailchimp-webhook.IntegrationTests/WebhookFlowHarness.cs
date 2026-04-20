using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Controllers;
using piano_mailchimp_webhook.Models;
using piano_mailchimp_webhook.Services;

namespace piano_mailchimp_webhook.IntegrationTests;

internal sealed class WebhookFlowHarness : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TestLogSink _logSink = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly PianoWebhookController _controller;
    private readonly InMemoryPianoWebhookEventStore _eventStore;

    public WebhookFlowHarness(
        PianoUserProfile? pianoUser,
        HttpStatusCode mailchimpStatusCode = HttpStatusCode.OK,
        string mailchimpResponseBody = "{}")
    {
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(_logSink);
        });

        var pianoHandler = new RecordingHttpMessageHandler((_, _) =>
        {
            if (pianoUser is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var responseBody = JsonSerializer.Serialize(pianoUser, JsonOptions);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        });

        var mailchimpHandler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(mailchimpStatusCode)
            {
                Content = new StringContent(mailchimpResponseBody, Encoding.UTF8, "application/json")
            }));

        PianoRequests = pianoHandler.Requests;
        MailchimpRequests = mailchimpHandler.Requests;

        var pianoApiClient = new PianoApiClient(
            new HttpClient(pianoHandler),
            Options.Create(new PianoOptions
            {
                BaseUrl = "https://piano.example.test",
                ApiToken = "test-piano-token",
                ApplicationId = "test-application"
            }),
            _loggerFactory.CreateLogger<PianoApiClient>());

        var mailchimpAudienceService = new MailchimpAudienceService(
            new HttpClient(mailchimpHandler),
            Options.Create(new MailchimpOptions
            {
                ApiKey = "test-mailchimp-key",
                ServerPrefix = "us1",
                AudienceId = "test-audience"
            }),
            _loggerFactory.CreateLogger<MailchimpAudienceService>());

        var newsletterPreferenceMapper = new NewsletterPreferenceMapper(
            Options.Create(new NewsletterMappingOptions
            {
                FieldMappings =
                [
                    new NewsletterFieldMapping
                    {
                        PianoFieldName = "daily_news",
                        MailchimpInterestId = "interest-daily"
                    },
                    new NewsletterFieldMapping
                    {
                        PianoFieldName = "sports_news",
                        MailchimpInterestId = "interest-sports"
                    }
                ]
            }));

        var processor = new PianoWebhookProcessor(
            pianoApiClient,
            mailchimpAudienceService,
            newsletterPreferenceMapper,
            _loggerFactory.CreateLogger<PianoWebhookProcessor>());

        _eventStore = new InMemoryPianoWebhookEventStore();
        _controller = new PianoWebhookController(
            _eventStore,
            processor,
            _loggerFactory.CreateLogger<PianoWebhookController>());
    }

    public IReadOnlyList<CapturedHttpRequest> PianoRequests { get; }

    public IReadOnlyList<CapturedHttpRequest> MailchimpRequests { get; }

    public IReadOnlyList<TestLogEntry> Logs => _logSink.GetSnapshot();

    public async Task<IActionResult> SendWebhookAsync(
        PianoWebhookEvent webhookEvent,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(webhookEvent, JsonOptions);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.ContentLength = payloadBytes.Length;
        httpContext.Request.Body = new MemoryStream(payloadBytes);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        return await _controller.Receive(cancellationToken);
    }

    public async Task<IReadOnlyList<PianoWebhookEventRecord>> ReadStoredRecordsAsync(
        CancellationToken cancellationToken = default)
    {
        return await _eventStore.ReadAllAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _loggerFactory.Dispose();

        return ValueTask.CompletedTask;
    }
}

internal sealed record CapturedHttpRequest(HttpMethod Method, Uri RequestUri, string? Body);

internal sealed class RecordingHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    public List<CapturedHttpRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new CapturedHttpRequest(
            request.Method,
            request.RequestUri ?? throw new InvalidOperationException("Request URI was missing."),
            body));

        return await responder(request, cancellationToken);
    }
}

internal sealed record TestLogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

internal sealed class TestLogSink : ILoggerProvider
{
    private readonly ConcurrentQueue<TestLogEntry> _entries = new();

    public ILogger CreateLogger(string categoryName)
    {
        return new TestLogger(categoryName, _entries);
    }

    public IReadOnlyList<TestLogEntry> GetSnapshot()
    {
        return _entries.ToArray();
    }

    public void Dispose()
    {
    }

    private sealed class TestLogger(
        string categoryName,
        ConcurrentQueue<TestLogEntry> entries) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NoOpScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue(new TestLogEntry(
                categoryName,
                logLevel,
                formatter(state, exception),
                exception));
        }
    }

    private sealed class NoOpScope : IDisposable
    {
        public static NoOpScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
