using Amazon.Lambda.CloudWatchEvents.ScheduledEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Models;
using piano_mailchimp_webhook.Services;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace piano_mailchimp_paid_checker;

public sealed class Function
{
    private static readonly Lazy<IHost> Host = new(BuildHost);

    public async Task<PaidAccessReconciliationSummary> ReconcileAsync(
        ScheduledEvent scheduledEvent,
        ILambdaContext context)
    {
        using var scope = Host.Value.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPaidAccessReconciliationService>();

        context.Logger.LogInformation(
            $"Starting paid-access reconciliation for scheduled event {scheduledEvent.Id}.");

        return await service.ReconcileAsync();
    }

    public async Task<SubscriberIdentityBackfillSummary> BackfillSubscriberIdentitiesAsync(
        SubscriberIdentityBackfillRequest? input,
        ILambdaContext context)
    {
        using var scope = Host.Value.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISubscriberIdentityBackfillService>();

        context.Logger.LogInformation("Starting subscriber identity backfill.");

        return await service.BackfillAsync(input);
    }

    private static IHost BuildHost()
    {
        return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((context, configuration) =>
            {
                configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
                configuration.AddEnvironmentVariables();

                var builtConfiguration = configuration.Build();
                var secretId = builtConfiguration["AwsSecretsManager:SecretId"];
                if (!string.IsNullOrWhiteSpace(secretId))
                {
                    configuration.AddSecretsManagerSecret(
                        secretId,
                        builtConfiguration["AwsSecretsManager:Region"]);
                }
            })
            .ConfigureServices(services =>
            {
                services.AddPianoMailchimpServices();
            })
            .Build();
    }
}
