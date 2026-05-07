using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using piano_mailchimp_webhook.Config;

namespace piano_mailchimp_webhook.Services;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPianoMailchimpServices(this IServiceCollection services)
    {
        services.AddOptions<EventStoreOptions>()
            .BindConfiguration(EventStoreOptions.SectionName);
        services.AddOptions<MailchimpOptions>()
            .BindConfiguration(MailchimpOptions.SectionName);
        services.AddOptions<NewsletterMappingOptions>()
            .BindConfiguration(NewsletterMappingOptions.SectionName);
        services.AddOptions<PianoOptions>()
            .BindConfiguration(PianoOptions.SectionName);

        services.AddHttpClient<IMailchimpAudienceService, MailchimpAudienceService>((serviceProvider, httpClient) =>
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

        services.AddHttpClient<IPianoApiClient, PianoApiClient>((serviceProvider, httpClient) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<PianoOptions>>().Value;

            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                httpClient.BaseAddress = new Uri($"{options.BaseUrl.TrimEnd('/')}/");
            }
        });

        services.AddSingleton<IPianoWebhookEventStore, PianoWebhookEventStore>();
        services.AddSingleton<INewsletterPreferenceMapper, NewsletterPreferenceMapper>();
        services.AddSingleton<IPianoWebhookDataParser, PianoWebhookDataParser>();
        services.AddScoped<IPianoWebhookProcessor, PianoWebhookProcessor>();
        services.AddScoped<IPianoWebhookService, PianoWebhookService>();

        return services;
    }
}
