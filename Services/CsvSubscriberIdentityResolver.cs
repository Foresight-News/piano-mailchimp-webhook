using System.Text;
using Microsoft.Extensions.Options;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public sealed class CsvSubscriberIdentityResolver(
    IOptions<SubscriberIdentityBackfillOptions> options,
    ILogger<CsvSubscriberIdentityResolver> logger) : ISubscriberIdentityResolver
{
    private readonly Lazy<IReadOnlyDictionary<string, SubscriberIdentityResolution>> _mapping =
        new(() => LoadMapping(options.Value, logger));

    public Task<SubscriberIdentityResolution> ResolveAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return Task.FromResult(SubscriberIdentityResolution.NotFound);
        }

        var normalizedEmail = NormalizeEmail(email);
        return Task.FromResult(
            _mapping.Value.TryGetValue(normalizedEmail, out var resolution)
                ? resolution
                : SubscriberIdentityResolution.NotFound);
    }

    private static IReadOnlyDictionary<string, SubscriberIdentityResolution> LoadMapping(
        SubscriberIdentityBackfillOptions options,
        ILogger logger)
    {
        var rows = ReadRows(options).ToList();

        if (rows.Count == 0)
        {
            throw new InvalidOperationException(
                "SubscriberIdentityBackfill:MappingCsvPath or SubscriberIdentityBackfill:MappingCsvContent is required.");
        }

        var header = rows[0];
        var emailIndex = FindHeaderIndex(header, "email", "email_address", "mailchimp_email");
        var uidIndex = FindHeaderIndex(header, "uid", "piano_uid", "pianoid", "piano_id");

        if (emailIndex < 0 || uidIndex < 0)
        {
            throw new InvalidOperationException(
                "Subscriber identity mapping CSV must contain email and uid columns.");
        }

        var grouped = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.Skip(1))
        {
            if (emailIndex >= row.Count || uidIndex >= row.Count)
            {
                continue;
            }

            var email = NormalizeEmail(row[emailIndex]);
            var uid = row[uidIndex].Trim();

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(uid))
            {
                continue;
            }

            if (!grouped.TryGetValue(email, out var uids))
            {
                uids = new HashSet<string>(StringComparer.Ordinal);
                grouped[email] = uids;
            }

            uids.Add(uid);
        }

        var mapping = new Dictionary<string, SubscriberIdentityResolution>(StringComparer.OrdinalIgnoreCase);

        foreach (var (email, uids) in grouped)
        {
            mapping[email] = uids.Count == 1
                ? SubscriberIdentityResolution.Found(uids.Single())
                : SubscriberIdentityResolution.Ambiguous;
        }

        logger.LogInformation(
            "Loaded {MappingCount} subscriber identity mappings.",
            mapping.Count);

        return mapping;
    }

    private static IEnumerable<List<string>> ReadRows(SubscriberIdentityBackfillOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.MappingCsvContent))
        {
            using var reader = new StringReader(options.MappingCsvContent);
            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    yield return ParseCsvLine(line);
                }
            }

            yield break;
        }

        if (string.IsNullOrWhiteSpace(options.MappingCsvPath))
        {
            yield break;
        }

        if (!File.Exists(options.MappingCsvPath))
        {
            throw new FileNotFoundException(
                "Subscriber identity mapping CSV was not found.",
                options.MappingCsvPath);
        }

        foreach (var line in File.ReadLines(options.MappingCsvPath))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return ParseCsvLine(line);
            }
        }
    }

    private static int FindHeaderIndex(IReadOnlyList<string> header, params string[] acceptedNames)
    {
        for (var i = 0; i < header.Count; i++)
        {
            var normalized = header[i].Trim().Replace(" ", "_", StringComparison.Ordinal).ToLowerInvariant();
            if (acceptedNames.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];

            if (character == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (character == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        values.Add(current.ToString());
        return values;
    }
}
