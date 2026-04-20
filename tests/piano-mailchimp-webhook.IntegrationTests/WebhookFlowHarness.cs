using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
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
    private readonly string _contentRoot;
    private readonly TestLogSink _logSink = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly PianoWebhookController _controller;

    public WebhookFlowHarness(
        PianoUserProfile? pianoUser,
        HttpStatusCode mailchimpStatusCode = HttpStatusCode.OK,
        string mailchimpResponseBody = "{}")
    {
        _contentRoot = Path.Combine(
            Path.GetTempPath(),
            "piano-mailchimp-webhook-tests",
            Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(_contentRoot);

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

        var eventStore = new PianoWebhookEventStore(new TestHostEnvironment(_contentRoot));
        _controller = new PianoWebhookController(
            eventStore,
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
        var storageDirectory = Path.Combine(_contentRoot, "App_Data", "PianoWebhookEvents");
        if (!Directory.Exists(storageDirectory))
        {
            return [];
        }

        var records = new List<PianoWebhookEventRecord>();

        foreach (var path in Directory.EnumerateFiles(storageDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            await using var inputStream = File.OpenRead(path);
            var record = await JsonSerializer.DeserializeAsync<PianoWebhookEventRecord>(
                inputStream,
                JsonOptions,
                cancellationToken);

            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records.OrderBy(record => record.ReceivedAt).ToArray();
    }

    public ValueTask DisposeAsync()
    {
        _loggerFactory.Dispose();

        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }

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

internal sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "piano-mailchimp-webhook.IntegrationTests";
    public string ContentRootPath { get; set; } = contentRootPath;
    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
}
