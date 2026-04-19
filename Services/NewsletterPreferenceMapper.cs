using System.Globalization;
using Microsoft.Extensions.Options;
using piano_mailchimp_webhook.Config;
using piano_mailchimp_webhook.Models;

namespace piano_mailchimp_webhook.Services;

public sealed class NewsletterPreferenceMapper(
    IOptions<NewsletterMappingOptions> options) : INewsletterPreferenceMapper
{
    private readonly NewsletterMappingOptions _options = options.Value;

    public Dictionary<string, bool> BuildInterestMap(PianoUserProfile user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var interestMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in _options.FieldMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.PianoFieldName) ||
                string.IsNullOrWhiteSpace(mapping.MailchimpInterestId))
            {
                continue;
            }

            var fieldValue = TryGetCustomFieldValue(user.CustomFields, mapping.PianoFieldName);
            interestMap[mapping.MailchimpInterestId] = ConvertToBool(fieldValue);
        }

        return interestMap;
    }

    public bool AnyManagedFieldChanged(IReadOnlyList<string> updatedFields)
    {
        if (updatedFields.Count == 0)
        {
            return false;
        }

        var managedFields = _options.FieldMappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.PianoFieldName))
            .Select(mapping => mapping.PianoFieldName.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return updatedFields
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .Select(field => field.Trim())
            .Any(managedFields.Contains);
    }

    private static object? TryGetCustomFieldValue(
        IReadOnlyDictionary<string, object?> customFields,
        string pianoFieldName)
    {
        if (customFields.TryGetValue(pianoFieldName, out var value))
        {
            return value;
        }

        foreach (var entry in customFields)
        {
            if (string.Equals(entry.Key, pianoFieldName, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return null;
    }

    private static bool ConvertToBool(object? value)
    {
        return value switch
        {
            bool boolValue => boolValue,
            string stringValue => ParseStringValue(stringValue),
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            decimal decimalValue => decimalValue != 0,
            _ => false
        };
    }

    private static bool ParseStringValue(string value)
    {
        var normalized = value.Trim();

        if (normalized.Length == 0)
        {
            return false;
        }

        if (bool.TryParse(normalized, out var boolValue))
        {
            return boolValue;
        }

        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            return intValue != 0;
        }

        if (long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            return longValue != 0;
        }

        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue))
        {
            return decimalValue != 0;
        }

        return normalized.ToLowerInvariant() switch
        {
            "y" => true,
            "yes" => true,
            "on" => true,
            "enabled" => true,
            "enable" => true,
            "t" => true,
            _ => false
        };
    }
}
