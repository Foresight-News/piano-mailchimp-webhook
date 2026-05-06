# piano-mailchimp-webhook

See [ARCHITECTURE.md](ARCHITECTURE.md) for diagrams covering the webhook app,
SAM scheduled reconciliation app, and subscriber identity backfill flow.

## Paid access reconciliation

This repo also contains a SAM-deployable Lambda app in
`src/piano-mailchimp-paid-checker`. It has two handlers:

- `ReconcileAsync`: scheduled nightly by `template.yaml`; checks Mailchimp
  members in the configured `PAID` segment and removes the `PAID` tag only when
  `PIANOID` exists and Piano reports no active access.
- `BackfillSubscriberIdentitiesAsync`: manual one-time handler; fills missing
  `PIANOID` merge fields from either a CSV mapping of email to Piano UID or a
  Piano user lookup by email.

Recommended rollout:

1. Configure a Mailchimp saved segment for contacts tagged `PAID`.
2. Configure `PaidAccessReconciliation:PaidTagSegmentId`. For SAM deploys,
   this can be supplied with the `PaidTagSegmentId` parameter, which maps to
   `PaidAccessReconciliation__PaidTagSegmentId` on the Lambda.
3. Run the backfill with `SubscriberIdentityBackfill:DryRun` set to `true`.
4. Review the summary for `NotFound` and `Ambiguous` rows.
5. Run the backfill with `SubscriberIdentityBackfill:DryRun` set to `false`.
6. Run reconciliation with `PaidAccessReconciliation:DryRun` set to `true`.
7. Set `PaidAccessReconciliation:DryRun` to `false` after the dry-run output is
   acceptable.

For large PAID segments, invoke `BackfillSubscriberIdentitiesAsync` in batches
so each Lambda run stays under the 15-minute execution limit. The optional
payload accepts an `offset` and `limit`:

```json
{
  "offset": 0,
  "limit": 250
}
```

Use the returned `NextOffset` as the next invocation's `offset` while `HasMore`
is `true`.

When invoking from the AWS CLI, the Lambda function can continue running after
the CLI's default socket read timeout expires. If you see `Read timeout on
endpoint URL` for the Lambda `/invocations` endpoint, retry with a smaller
batch and set the CLI read timeout to match the Lambda timeout:

```bash
aws lambda invoke \
  --region eu-west-1 \
  --function-name arn:aws:lambda:eu-west-1:419139139995:function:mc-subs-check-SubscriberIdentityBackfillFunction-hlXMIZbp70hJ \
  --payload '{"offset":0,"limit":100}' \
  --cli-binary-format raw-in-base64-out \
  --cli-read-timeout 900 \
  response.json
```

By default, `SubscriberIdentityBackfill:ResolverSource` is `Piano`, which
resolves missing `PIANOID` values by searching Piano users by email. The Piano
resolver only updates Mailchimp when Piano returns exactly one user with an
exact email match; zero matches are counted as `NotFound`, and multiple
distinct UIDs are counted as `Ambiguous`.

Set `SubscriberIdentityBackfill:ResolverSource` to `Csv` to backfill from a CSV
mapping instead. The backfill CSV must have headers containing an email column
(`email`, `email_address`, or `mailchimp_email`) and a UID column (`uid`,
`piano_uid`, `pianoid`, or `piano_id`). Provide it as either
`SubscriberIdentityBackfill:MappingCsvPath` for local/manual runs or
`SubscriberIdentityBackfill:MappingCsvContent` in the deployment secret for the
Lambda backfill.

Example secret shape:

```json
{
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
  "PaidAccessReconciliation": {
    "PaidTagName": "PAID",
    "PaidTagSegmentId": "7232611",
    "BatchSize": 100,
    "DryRun": true
  },
  "SubscriberIdentityBackfill": {
    "ResolverSource": "Piano",
    "MappingCsvPath": "",
    "MappingCsvContent": "",
    "PianoIdMergeFieldName": "PIANOID",
    "DryRun": true
  }
}
```
