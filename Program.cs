using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddProductionSecretsManager();

builder.Services.AddControllers();
builder.Services.AddPianoMailchimpServices();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "piano-mailchimp-webhook",
    status = "ready"
}));

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapControllers();

app.Run();
