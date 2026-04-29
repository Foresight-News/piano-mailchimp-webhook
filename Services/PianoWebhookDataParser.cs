using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public sealed class PianoWebhookDataParser(IOptions<PianoOptions> options) : IPianoWebhookDataParser
{
    private const string Delimiter = "~~~";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PianoWebhookEvent Parse(string encryptedData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedData);

        var privateKey = options.Value.PrivateKey;
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            throw new InvalidOperationException("Piano PrivateKey is not configured.");
        }

        var decryptedJson = Decrypt(privateKey, encryptedData);
        var webhookEvent = JsonSerializer.Deserialize<PianoWebhookEvent>(decryptedJson, JsonOptions);

        return NormalizeWebhookEvent(webhookEvent)
            ?? throw new JsonException("Piano webhook data could not be deserialized.");
    }

    private static string Decrypt(string privateKey, string encryptedData)
    {
        var parts = encryptedData.Split(Delimiter, StringSplitOptions.None);
        if (parts.Length > 2)
        {
            throw new InvalidOperationException("Piano webhook data contains more than one HMAC segment.");
        }

        var encryptedPayload = parts[0];
        if (parts.Length == 2)
        {
            VerifyHmac(privateKey, encryptedPayload, parts[1]);
        }

        var encryptedBytes = Base64UrlDecode(encryptedPayload);
        var cipherKey = BuildCipherKey(privateKey);

        using var aes = Aes.Create();
        aes.Key = cipherKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        using var decryptor = aes.CreateDecryptor();
        var decryptedBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
        var unpaddedBytes = Unpad(decryptedBytes);

        return Encoding.UTF8.GetString(unpaddedBytes);
    }

    private static void VerifyHmac(string privateKey, string encryptedPayload, string expectedHmac)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(privateKey));
        var actualHmac = Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(encryptedPayload)));

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualHmac),
                Encoding.ASCII.GetBytes(expectedHmac)))
        {
            throw new InvalidOperationException("Piano webhook data HMAC is invalid.");
        }
    }

    private static byte[] BuildCipherKey(string privateKey)
    {
        var cipherKey = privateKey.Length > 32
            ? privateKey[..32]
            : privateKey.PadRight(32, 'X');

        return Encoding.UTF8.GetBytes(cipherKey);
    }

    private static byte[] Unpad(byte[] data)
    {
        if (data.Length == 0)
        {
            return data;
        }

        var paddingLength = data[^1];
        if (paddingLength is <= 0 or > 16 || paddingLength > data.Length)
        {
            return data;
        }

        return data[..^paddingLength];
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var paddedValue = value + new string('=', (4 - value.Length % 4) % 4);
        return Convert.FromBase64String(paddedValue.Replace('-', '+').Replace('_', '/'));
    }

    private static string Base64UrlEncode(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static PianoWebhookEvent? NormalizeWebhookEvent(PianoWebhookEvent? webhookEvent)
    {
        if (webhookEvent is null || !string.IsNullOrWhiteSpace(webhookEvent.Event))
        {
            return webhookEvent;
        }

        return new PianoWebhookEvent
        {
            Type = webhookEvent.Type,
            Event = webhookEvent.Type,
            Uid = webhookEvent.Uid,
            Aid = webhookEvent.Aid,
            Timestamp = webhookEvent.Timestamp,
            UpdatedCustomFields = webhookEvent.UpdatedCustomFields
        };
    }
}
