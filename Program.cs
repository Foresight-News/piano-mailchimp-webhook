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
builder.Services.AddHttpClient<IMailchimpAudienceService, MailchimpAudienceService>();
builder.Services.AddHttpClient<IPianoApiClient, PianoApiClient>();
builder.Services.AddSingleton<INewsletterPreferenceMapper, NewsletterPreferenceMapper>();
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
