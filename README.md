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

## Piano Subscriber Export SAM App

This repo also contains a SAM app with one Python 3.12 Lambda function that
exports current Piano subscribers to CSV in S3.

- Template: `template.yaml`
- Function code: `src/piano-subscriber-exporter/app.py`
- Bucket created by CloudFormation: see stack output `SubscriberExportBucketName`
- Output key pattern: `piano/subscribers/subscribers-YYYYMMDD-HHMMSS.csv`

The function reads Piano credentials and export settings from AWS Secrets
Manager secret `piano-mailchimp-webhook/production`. The template grants the
Lambda read access to that secret and does not expose the Piano API token as a
CloudFormation parameter.

Expected secret fields:

```json
{
  "Piano": {
    "ApiToken": "",
    "ApplicationId": "28C3eb1vpu"
  },
  "PianoSubscriberExport": {
    "Source": "VX",
    "PageLimit": 1000,
    "MaxPages": 100
  }
}
```

`PianoSubscriberExport` is optional. `PageLimit` defaults to `1000`, and
`MaxPages` defaults to `100`.

Example deploy:

```bash
sam deploy --guided
```

Example manual invoke after deploy:

```bash
aws lambda invoke \
  --function-name <function-name> \
  response.json
```
