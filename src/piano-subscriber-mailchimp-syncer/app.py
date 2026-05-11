import base64
import csv
import hashlib
import io
import json
import logging
import os
from urllib.parse import quote, unquote_plus


LOGGER = logging.getLogger()
LOGGER.setLevel(logging.INFO)

DEFAULT_SECRET_ID = "piano-mailchimp-webhook/production"
MAILCHIMP_TAG_NAME = "PAID"
DEFAULT_BATCH_SIZE = 10
DEFAULT_MAILCHIMP_CONNECT_TIMEOUT_SECONDS = 2.0
DEFAULT_MAILCHIMP_READ_TIMEOUT_SECONDS = 5.0


def lambda_handler(event, context):
    import boto3

    s3 = boto3.client("s3")
    sqs = boto3.client("sqs")
    queue_url = _read_required_env("SYNC_QUEUE_URL")
    batch_size = _read_positive_int_env("SYNC_BATCH_SIZE", DEFAULT_BATCH_SIZE)

    queued_batches = 0
    queued_rows = 0
    skipped_rows = 0

    for record in event.get("Records", []):
        bucket_name, key = _read_s3_location(record)
        csv_body = read_s3_object_text(s3, bucket_name, key)
        rows, result = build_subscriber_batches(csv_body)
        skipped_rows += result["skipped_rows"]

        for batch_number, batch in enumerate(chunk_rows(rows, batch_size), start=1):
            sqs.send_message(
                QueueUrl=queue_url,
                MessageBody=json.dumps(
                    {
                        "bucket": bucket_name,
                        "key": key,
                        "batch_number": batch_number,
                        "rows": batch,
                    }
                ),
            )
            queued_batches += 1
            queued_rows += len(batch)

        LOGGER.info(
            "Queued s3://%s/%s for Mailchimp sync. queued_rows=%s skipped_rows=%s batch_size=%s",
            bucket_name,
            key,
            len(rows),
            result["skipped_rows"],
            batch_size,
        )

    return {
        "queued_batches": queued_batches,
        "queued_rows": queued_rows,
        "skipped_rows": skipped_rows,
    }


def worker_handler(event, context):
    mailchimp_client = build_mailchimp_client()
    processed_rows = 0
    skipped_rows = 0

    for record in event.get("Records", []):
        message = json.loads(record["body"])
        rows = message.get("rows", [])
        result = sync_subscriber_rows(rows, mailchimp_client)
        processed_rows += result["processed_rows"]
        skipped_rows += result["skipped_rows"]

        LOGGER.info(
            "Synced Mailchimp batch. bucket=%s key=%s batch_number=%s processed_rows=%s skipped_rows=%s",
            message.get("bucket"),
            message.get("key"),
            message.get("batch_number"),
            result["processed_rows"],
            result["skipped_rows"],
        )

    return {
        "processed_rows": processed_rows,
        "skipped_rows": skipped_rows,
    }


def build_mailchimp_client():
    import boto3
    import urllib3

    secret_id = os.environ.get("SECRET_ID", DEFAULT_SECRET_ID)
    secret_region = os.environ.get("SECRET_REGION") or os.environ.get("AWS_REGION")

    secrets_manager = boto3.client("secretsmanager", region_name=secret_region)
    config = load_secret_config(secrets_manager, secret_id)
    mailchimp_config = _get_dict(config, "Mailchimp")

    connect_timeout = _read_positive_float_env(
        "MAILCHIMP_CONNECT_TIMEOUT_SECONDS",
        DEFAULT_MAILCHIMP_CONNECT_TIMEOUT_SECONDS,
    )
    read_timeout = _read_positive_float_env(
        "MAILCHIMP_READ_TIMEOUT_SECONDS",
        DEFAULT_MAILCHIMP_READ_TIMEOUT_SECONDS,
    )

    return MailchimpClient(
        http=urllib3.PoolManager(),
        api_key=_read_required_config(mailchimp_config, "ApiKey", "Mailchimp:ApiKey"),
        server_prefix=_read_required_config(
            mailchimp_config,
            "ServerPrefix",
            "Mailchimp:ServerPrefix",
        ),
        audience_id=_read_required_config(
            mailchimp_config,
            "AudienceId",
            "Mailchimp:AudienceId",
        ),
        request_timeout=urllib3.Timeout(connect=connect_timeout, read=read_timeout),
    )


def load_secret_config(secrets_manager, secret_id):
    response = secrets_manager.get_secret_value(SecretId=secret_id)
    secret_string = response.get("SecretString")

    if not secret_string:
        raise RuntimeError(f"{secret_id} did not contain a SecretString value.")

    config = json.loads(secret_string)
    if not isinstance(config, dict):
        raise RuntimeError(f"{secret_id} SecretString must be a JSON object.")

    return config


def read_s3_object_text(s3, bucket_name, key):
    response = s3.get_object(Bucket=bucket_name, Key=key)
    body = response["Body"].read()
    return body.decode("utf-8-sig")


def sync_subscriber_csv(csv_body, mailchimp_client):
    reader = csv.DictReader(io.StringIO(csv_body))
    rows = list(reader)
    return sync_subscriber_rows(rows, mailchimp_client, start_row_number=2)


def sync_subscriber_rows(rows, mailchimp_client, start_row_number=1):
    processed_rows = 0
    skipped_rows = 0

    for row_number, row in enumerate(rows, start=start_row_number):
        email = _get_row_text(row, "email")
        if not email:
            skipped_rows += 1
            LOGGER.warning("Skipping CSV row %s because email is missing.", row_number)
            continue

        first_name = _get_row_text(row, "first_name")
        last_name = _get_row_text(row, "last_name")

        mailchimp_client.upsert_member(
            email=email,
            first_name=first_name,
            last_name=last_name,
        )
        mailchimp_client.ensure_paid_tag(email)
        processed_rows += 1

    return {
        "processed_rows": processed_rows,
        "skipped_rows": skipped_rows,
    }


def build_subscriber_batches(csv_body):
    reader = csv.DictReader(io.StringIO(csv_body))
    rows = []
    skipped_rows = 0

    for row_number, row in enumerate(reader, start=2):
        email = _get_row_text(row, "email")
        if not email:
            skipped_rows += 1
            LOGGER.warning("Skipping CSV row %s because email is missing.", row_number)
            continue

        rows.append(
            {
                "email": email,
                "first_name": _get_row_text(row, "first_name"),
                "last_name": _get_row_text(row, "last_name"),
            }
        )

    return rows, {
        "skipped_rows": skipped_rows,
    }


def chunk_rows(rows, batch_size):
    for index in range(0, len(rows), batch_size):
        yield rows[index : index + batch_size]


class MailchimpClient:
    def __init__(self, http, api_key, server_prefix, audience_id, request_timeout=None):
        self.http = http
        self.api_key = api_key
        self.server_prefix = server_prefix
        self.audience_id = audience_id
        self.request_timeout = request_timeout
        self.base_url = f"https://{server_prefix}.api.mailchimp.com/3.0"
        self.headers = {
            "Authorization": _build_basic_auth_header(api_key),
            "Content-Type": "application/json",
        }

    def upsert_member(self, email, first_name, last_name):
        subscriber_hash = build_subscriber_hash(email)
        body = {
            "email_address": email.strip(),
            "status_if_new": "subscribed",
            "merge_fields": {
                "FNAME": first_name,
                "LNAME": last_name,
            },
        }
        url = self._member_url(subscriber_hash)
        self._request_json("PUT", url, body)

    def ensure_paid_tag(self, email):
        subscriber_hash = build_subscriber_hash(email)
        body = {
            "tags": [
                {
                    "name": MAILCHIMP_TAG_NAME,
                    "status": "active",
                }
            ]
        }
        url = f"{self._member_url(subscriber_hash)}/tags"
        self._request_json("POST", url, body)

    def _member_url(self, subscriber_hash):
        list_id = quote(self.audience_id, safe="")
        return f"{self.base_url}/lists/{list_id}/members/{subscriber_hash}"

    def _request_json(self, method, url, body):
        response = self.http.request(
            method,
            url,
            body=json.dumps(body).encode("utf-8"),
            headers=self.headers,
            timeout=self.request_timeout,
        )

        if response.status < 200 or response.status >= 300:
            response_body = response.data.decode("utf-8", errors="replace")
            raise RuntimeError(
                f"Mailchimp request failed with status {response.status}: {response_body}"
            )


def build_subscriber_hash(email):
    normalized_email = email.strip().lower()
    if not normalized_email:
        raise ValueError("Email address is required.")

    return hashlib.md5(normalized_email.encode("utf-8")).hexdigest()


def _read_s3_location(record):
    bucket_name = record.get("s3", {}).get("bucket", {}).get("name")
    key = record.get("s3", {}).get("object", {}).get("key")

    if not bucket_name or not key:
        raise RuntimeError("S3 event record must contain bucket name and object key.")

    return bucket_name, unquote_plus(key)


def _get_row_text(row, key):
    value = row.get(key)
    if value is None:
        return ""

    return str(value).strip()


def _read_required_env(name):
    value = os.environ.get(name)
    if not value:
        raise RuntimeError(f"{name} environment variable is required.")
    return value


def _read_positive_int_env(name, default):
    raw_value = os.environ.get(name)
    if raw_value is None or raw_value == "":
        return default

    value = int(raw_value)
    if value <= 0:
        raise RuntimeError(f"{name} must be greater than zero.")

    return value


def _read_positive_float_env(name, default):
    raw_value = os.environ.get(name)
    if raw_value is None or raw_value == "":
        return default

    value = float(raw_value)
    if value <= 0:
        raise RuntimeError(f"{name} must be greater than zero.")

    return value


def _read_required_config(config, key, description):
    value = config.get(key)
    if value is None or value == "":
        raise RuntimeError(f"{description} is required in Secrets Manager config.")

    return str(value).strip()


def _get_dict(config, key):
    value = config.get(key)
    return value if isinstance(value, dict) else {}


def _build_basic_auth_header(api_key):
    credentials = base64.b64encode(f"anystring:{api_key}".encode("ascii")).decode("ascii")
    return f"Basic {credentials}"
