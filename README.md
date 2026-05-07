# piano-mailchimp-webhook

ASP.NET Core webhook endpoint for syncing Piano user updates into Mailchimp.

## Endpoints

- `GET /` returns service readiness metadata.
- `GET /health` returns a basic health response.
- `GET /webhooks/piano?data=...` receives encrypted Piano webhook payloads.
- `POST /webhooks/piano` receives JSON or form-encoded Piano webhook payloads.

## Configuration

The app reads configuration from `appsettings.json`, environment variables, and
AWS Secrets Manager when `AwsSecretsManager:SecretId` is configured.

Required sections:

```json
{
  "EventStore": {
    "ConnectionString": "",
    "Schema": "dbo",
    "TableName": "PianoWebhookEvents"
  },
  "Mailchimp": {
    "ApiKey": "",
    "ServerPrefix": "",
    "AudienceId": ""
  },
  "Piano": {
    "BaseUrl": "https://api.piano.io",
    "ApiToken": "",
    "ApplicationId": "",
    "PrivateKey": ""
  },
  "NewsletterMapping": {
    "FieldMappings": []
  }
}
```

See [ARCHITECTURE.md](ARCHITECTURE.md) for the webhook processing flow.
