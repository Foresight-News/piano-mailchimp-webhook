# piano-mailchimp-webhook

## Paid access reconciliation

This repo also contains a SAM-deployable Lambda app in
`src/piano-mailchimp-paid-checker`. It has two handlers:

- `ReconcileAsync`: scheduled nightly by `template.yaml`; checks Mailchimp
  members in the configured `PAID` segment and removes the `PAID` tag only when
  `PIANOID` exists and Piano reports no active access.
- `BackfillSubscriberIdentitiesAsync`: manual one-time handler; fills missing
  `PIANOID` merge fields from a CSV mapping of email to Piano UID.

Recommended rollout:

1. Configure a Mailchimp saved segment for contacts tagged `PAID`.
2. Configure `PaidAccessReconciliation:PaidTagSegmentId`.
3. Run the backfill with `SubscriberIdentityBackfill:DryRun` set to `true`.
4. Review the summary for `NotFound` and `Ambiguous` rows.
5. Run the backfill with `SubscriberIdentityBackfill:DryRun` set to `false`.
6. Run reconciliation with `PaidAccessReconciliation:DryRun` set to `true`.
7. Set `PaidAccessReconciliation:DryRun` to `false` after the dry-run output is
   acceptable.

The backfill CSV must have headers containing an email column (`email`,
`email_address`, or `mailchimp_email`) and a UID column (`uid`, `piano_uid`,
`pianoid`, or `piano_id`). Provide it as either
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
    "PaidTagSegmentId": "",
    "BatchSize": 100,
    "DryRun": true
  },
  "SubscriberIdentityBackfill": {
    "MappingCsvPath": "",
    "MappingCsvContent": "",
    "PianoIdMergeFieldName": "PIANOID",
    "DryRun": true
  }
}
```
