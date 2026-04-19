using System.Security.Cryptography;
using System.Text;

namespace piano_mailchimp_webhook.Services;

public static class SubscriberHash
{
    public static string FromEmail(string emailAddress)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
        {
            throw new ArgumentException("Email address is required.", nameof(emailAddress));
        }

        var normalizedEmail = emailAddress.Trim().ToLowerInvariant();
        var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(normalizedEmail));

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
