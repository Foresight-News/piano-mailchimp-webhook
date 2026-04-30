using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Configuration;

namespace piano_mailchimp_webhook.Config;

public static class AwsSecretsManagerConfigurationExtensions
{
    private const string SectionName = "AwsSecretsManager";

    public static WebApplicationBuilder AddProductionSecretsManager(this WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsProduction())
        {
            return builder;
        }

        var secretId = builder.Configuration[$"{SectionName}:SecretId"];
        if (string.IsNullOrWhiteSpace(secretId))
        {
            throw new InvalidOperationException(
                $"{SectionName}:SecretId must be configured when running in Production.");
        }

        var region = builder.Configuration[$"{SectionName}:Region"];
        builder.Configuration.AddSecretsManagerSecret(secretId, region);

        return builder;
    }

    public static IConfigurationBuilder AddSecretsManagerSecret(
        this IConfigurationBuilder configurationBuilder,
        string secretId,
        string? region = null)
    {
        using var client = CreateClient(region);
        var secretValue = client.GetSecretValueAsync(new GetSecretValueRequest
        {
            SecretId = secretId
        }).GetAwaiter().GetResult();

        var secretJson = GetSecretJson(secretValue);
        var flattenedValues = FlattenSecretJson(secretJson);

        configurationBuilder.AddInMemoryCollection(flattenedValues);

        return configurationBuilder;
    }

    private static IAmazonSecretsManager CreateClient(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return new AmazonSecretsManagerClient();
        }

        return new AmazonSecretsManagerClient(new AmazonSecretsManagerConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region)
        });
    }

    private static string GetSecretJson(GetSecretValueResponse secretValue)
    {
        if (!string.IsNullOrWhiteSpace(secretValue.SecretString))
        {
            return secretValue.SecretString;
        }

        if (secretValue.SecretBinary is null)
        {
            throw new InvalidOperationException("The AWS Secrets Manager secret did not contain a value.");
        }

        using var reader = new StreamReader(secretValue.SecretBinary, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Dictionary<string, string?> FlattenSecretJson(string secretJson)
    {
        using var document = JsonDocument.Parse(secretJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        FlattenElement(document.RootElement, parentPath: null, values);
        return values;
    }

    private static void FlattenElement(
        JsonElement element,
        string? parentPath,
        IDictionary<string, string?> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = CombinePath(parentPath, property.Name);
                    FlattenElement(property.Value, childPath, values);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    FlattenElement(item, CombinePath(parentPath, index.ToString()), values);
                    index++;
                }

                break;

            case JsonValueKind.String:
                values[parentPath ?? string.Empty] = element.GetString();
                break;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                values[parentPath ?? string.Empty] = element.GetRawText();
                break;

            case JsonValueKind.Null:
                values[parentPath ?? string.Empty] = null;
                break;
        }
    }

    private static string CombinePath(string? parentPath, string childPath)
    {
        return string.IsNullOrWhiteSpace(parentPath)
            ? childPath
            : $"{parentPath}:{childPath}";
    }
}
