using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.Configure<MailchimpOptions>(
    builder.Configuration.GetSection(MailchimpOptions.SectionName));
builder.Services.Configure<NewsletterMappingOptions>(
    builder.Configuration.GetSection(NewsletterMappingOptions.SectionName));
builder.Services.Configure<PianoOptions>(
    builder.Configuration.GetSection(PianoOptions.SectionName));
builder.Services.Configure<PianoWebhookOptions>(
    builder.Configuration.GetSection(PianoWebhookOptions.SectionName));
builder.Services.AddHttpClient<IMailchimpAudienceService, MailchimpAudienceService>((serviceProvider, httpClient) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<MailchimpOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(options.ServerPrefix))
    {
        httpClient.BaseAddress = new Uri($"https://{options.ServerPrefix}.api.mailchimp.com/3.0/");
    }

    if (!string.IsNullOrWhiteSpace(options.ApiKey))
    {
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"codex:{options.ApiKey}"));
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", credentials);
    }
});
builder.Services.AddHttpClient<IPianoApiClient, PianoApiClient>((serviceProvider, httpClient) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<PianoOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(options.BaseUrl))
    {
        httpClient.BaseAddress = new Uri($"{options.BaseUrl.TrimEnd('/')}/");
    }
});
builder.Services.AddSingleton<PianoWebhookEventStore>();
builder.Services.AddSingleton<INewsletterPreferenceMapper, NewsletterPreferenceMapper>();
builder.Services.AddScoped<IPianoWebhookProcessor, PianoWebhookProcessor>();
builder.Services.AddScoped<IPianoWebhookService, PianoWebhookService>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "piano-mailchimp-webhook",
    status = "ready"
}));

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapControllers();

app.Run();
