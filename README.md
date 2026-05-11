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

This repo also contains a SAM app with Python 3.12 Lambda functions that export
current Piano subscribers to CSV in S3 and sync those CSV rows to Mailchimp.

- Template: `template.yaml`
- Export function code: `src/piano-subscriber-exporter/app.py`
- Mailchimp sync function code: `src/piano-subscriber-mailchimp-syncer/app.py`
- Bucket created by CloudFormation: see stack output `SubscriberExportBucketName`
- Output key pattern: `piano/subscribers/subscribers-YYYYMMDD-HHMMSS.csv`
- Export schedule: every night at 1am GMT (`cron(0 1 * * ? *)`)
- Sync trigger: S3 `ObjectCreated` for `piano/subscribers/*.csv`
- Sync batching: the S3-triggered function splits CSV rows into SQS messages of
  10 subscribers, and a worker Lambda syncs one queued batch at a time.
- Mailchimp worker concurrency: the worker Lambda and SQS event source are
  capped at 2 concurrent invocations so SQS cannot fan out enough Mailchimp sync
  batches to exceed Mailchimp's simultaneous connection limit.

The functions read Piano, Mailchimp, and export settings from AWS Secrets
Manager secret `piano-mailchimp-webhook/production`. The template grants the
Lambdas read access to that secret where needed and does not expose API
credentials as CloudFormation parameters.

Expected secret fields:

```json
{
  "Piano": {
    "ApiToken": "",
    "ApplicationId": "28C3eb1vpu"
  },
  "Mailchimp": {
    "ApiKey": "",
    "ServerPrefix": "",
    "AudienceId": ""
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

The Mailchimp worker uses explicit HTTP timeouts so slow API calls fail instead
of running until the Lambda hard timeout. The default timeouts are 2 seconds to
connect and 5 seconds to read a response. Mailchimp transport failures and
`429 Too Many Requests` responses are retried in-process up to 3 times with
exponential backoff before the SQS message is allowed to fail and retry.

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
