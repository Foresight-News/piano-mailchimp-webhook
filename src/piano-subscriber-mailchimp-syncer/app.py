import base64
import csv
import hashlib
import io
import json
import logging
import os
import time
from urllib.parse import quote, unquote_plus, urlencode


LOGGER = logging.getLogger()
LOGGER.setLevel(logging.INFO)

DEFAULT_SECRET_ID = "piano-mailchimp-webhook/production"
MAILCHIMP_TAG_NAME = "PAID"
MAILCHIMP_EXPIRED_TAG_NAME = "EXPIRED"
DEFAULT_BATCH_SIZE = 10
DEFAULT_MAILCHIMP_PAGE_SIZE = 100
DEFAULT_MAILCHIMP_CONNECT_TIMEOUT_SECONDS = 2.0
DEFAULT_MAILCHIMP_READ_TIMEOUT_SECONDS = 15.0
DEFAULT_MAILCHIMP_MAX_RETRIES = 3
DEFAULT_MAILCHIMP_RETRY_DELAY_SECONDS = 1.0


def lambda_handler(event, context):
    import boto3

    s3 = boto3.client("s3")
    sqs = boto3.client("sqs")
    queue_url = _read_required_env("SYNC_QUEUE_URL")
    batch_size = _read_positive_int_env("SYNC_BATCH_SIZE", DEFAULT_BATCH_SIZE)

    queued_batches = 0
    queued_rows = 0
    skipped_rows = 0
    reconciliation_results = []
    mailchimp_client = None

    for record in event.get("Records", []):
        bucket_name, key = _read_s3_location(record)
        csv_body = read_s3_object_text(s3, bucket_name, key)
        rows, result = build_subscriber_batches(csv_body)
        skipped_rows += result["skipped_rows"]

        if mailchimp_client is None:
            mailchimp_client = build_mailchimp_client()

        reconciliation_result = reconcile_expired_paid_subscribers(
            build_email_set(rows),
            mailchimp_client,
        )
        reconciliation_results.append(reconciliation_result)

        record_queued_batches = 0
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
            record_queued_batches += 1
            queued_batches += 1
            queued_rows += len(batch)

        LOGGER.info(
            "Queued s3://%s/%s for Mailchimp sync. queued_batches=%s queued_rows=%s skipped_rows=%s batch_size=%s",
            bucket_name,
            key,
            record_queued_batches,
            len(rows),
            result["skipped_rows"],
            batch_size,
        )

    return {
        "queued_batches": queued_batches,
        "queued_rows": queued_rows,
        "skipped_rows": skipped_rows,
        "reconciliation": combine_reconciliation_results(reconciliation_results),
    }


def worker_handler(event, context):
    mailchimp_client = build_mailchimp_client()
    processed_rows = 0
    updated_rows = 0
    new_rows = 0
    skipped_rows = 0
    failed_rows = 0

    for record in event.get("Records", []):
        message = json.loads(record["body"])
        rows = message.get("rows", [])
        result = sync_subscriber_rows(rows, mailchimp_client)
        processed_rows += result["processed_rows"]
        updated_rows += result["updated_rows"]
        new_rows += result["new_rows"]
        skipped_rows += result["skipped_rows"]
        failed_rows += result["failed_rows"]

        LOGGER.info(
            "Synced Mailchimp batch. bucket=%s key=%s batch_number=%s processed_rows=%s updated_rows=%s new_rows=%s skipped_rows=%s failed_rows=%s",
            message.get("bucket"),
            message.get("key"),
            message.get("batch_number"),
            result["processed_rows"],
            result["updated_rows"],
            result["new_rows"],
            result["skipped_rows"],
            result["failed_rows"],
        )

    LOGGER.info(
        "Mailchimp sync summary. processed_rows=%s updated_rows=%s new_rows=%s skipped_rows=%s failed_rows=%s",
        processed_rows,
        updated_rows,
        new_rows,
        skipped_rows,
        failed_rows,
    )

    return {
        "processed_rows": processed_rows,
        "updated_rows": updated_rows,
        "new_rows": new_rows,
        "skipped_rows": skipped_rows,
        "failed_rows": failed_rows,
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
    max_retries = _read_non_negative_int_env(
        "MAILCHIMP_MAX_RETRIES",
        DEFAULT_MAILCHIMP_MAX_RETRIES,
    )
    retry_delay = _read_positive_float_env(
        "MAILCHIMP_RETRY_DELAY_SECONDS",
        DEFAULT_MAILCHIMP_RETRY_DELAY_SECONDS,
    )

    return MailchimpClient(
        http=urllib3.PoolManager(retries=False),
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
        max_retries=max_retries,
        retry_delay_seconds=retry_delay,
        request_exception_types=(urllib3.exceptions.HTTPError,),
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
    updated_rows = 0
    new_rows = 0
    skipped_rows = 0
    failed_rows = 0

    for row_number, row in enumerate(rows, start=start_row_number):
        email = _get_row_text(row, "email")
        if not email:
            skipped_rows += 1
            LOGGER.warning("Skipping CSV row %s because email is missing.", row_number)
            continue

        first_name = _get_row_text(row, "first_name")
        last_name = _get_row_text(row, "last_name")

        try:
            is_existing_member = mailchimp_client.member_exists(email)
            mailchimp_client.upsert_member(
                email=email,
                first_name=first_name,
                last_name=last_name,
            )
            mailchimp_client.ensure_paid_tag(email)
            processed_rows += 1
            if is_existing_member:
                updated_rows += 1
            else:
                new_rows += 1
        except Exception:
            failed_rows += 1
            LOGGER.exception("Failed to sync CSV row %s to Mailchimp.", row_number)

    return {
        "processed_rows": processed_rows,
        "updated_rows": updated_rows,
        "new_rows": new_rows,
        "skipped_rows": skipped_rows,
        "failed_rows": failed_rows,
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


def build_email_set(rows):
    emails = set()
    for row in rows:
        email = normalize_email(_get_row_text(row, "email"))
        if email:
            emails.add(email)

    return emails


def reconcile_expired_paid_subscribers(active_emails, mailchimp_client):
    result = {
        "active_emails": len(active_emails),
        "paid_members": 0,
        "expired_members": 0,
        "failed_members": 0,
        "skipped_blank_members": 0,
    }

    for member in mailchimp_client.list_tagged_members(MAILCHIMP_TAG_NAME):
        result["paid_members"] += 1
        email = normalize_email(member.get("email_address"))
        if not email:
            result["skipped_blank_members"] += 1
            continue

        if email in active_emails:
            continue

        try:
            mailchimp_client.ensure_expired_tag(email)
            result["expired_members"] += 1
        except Exception:
            result["failed_members"] += 1
            LOGGER.exception("Failed to add EXPIRED tag for Mailchimp member.")

    LOGGER.info(
        "Mailchimp paid subscriber reconciliation summary. active_emails=%s paid_members=%s expired_members=%s failed_members=%s skipped_blank_members=%s",
        result["active_emails"],
        result["paid_members"],
        result["expired_members"],
        result["failed_members"],
        result["skipped_blank_members"],
    )

    return result


def combine_reconciliation_results(results):
    combined = {
        "active_emails": 0,
        "paid_members": 0,
        "expired_members": 0,
        "failed_members": 0,
        "skipped_blank_members": 0,
    }

    for result in results:
        for key in combined:
            combined[key] += result.get(key, 0)

    return combined


def chunk_rows(rows, batch_size):
    for index in range(0, len(rows), batch_size):
        yield rows[index : index + batch_size]


class MailchimpClient:
    def __init__(
        self,
        http,
        api_key,
        server_prefix,
        audience_id,
        request_timeout=None,
        max_retries=DEFAULT_MAILCHIMP_MAX_RETRIES,
        retry_delay_seconds=DEFAULT_MAILCHIMP_RETRY_DELAY_SECONDS,
        retry_sleep=time.sleep,
        request_exception_types=(),
    ):
        self.http = http
        self.api_key = api_key
        self.server_prefix = server_prefix
        self.audience_id = audience_id
        self.request_timeout = request_timeout
        self.max_retries = max_retries
        self.retry_delay_seconds = retry_delay_seconds
        self.retry_sleep = retry_sleep
        self.request_exception_types = request_exception_types
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

    def member_exists(self, email):
        subscriber_hash = build_subscriber_hash(email)
        response = self._request_json(
            "GET",
            self._member_url(subscriber_hash),
            allow_statuses={404},
        )
        return response.status != 404

    def ensure_paid_tag(self, email):
        self.ensure_tag(email, MAILCHIMP_TAG_NAME)

    def ensure_expired_tag(self, email):
        self.ensure_tag(email, MAILCHIMP_EXPIRED_TAG_NAME)

    def ensure_tag(self, email, tag_name):
        subscriber_hash = build_subscriber_hash(email)
        body = {
            "tags": [
                {
                    "name": tag_name,
                    "status": "active",
                }
            ]
        }
        url = f"{self._member_url(subscriber_hash)}/tags"
        self._request_json("POST", url, body)

    def list_tagged_members(self, tag_name):
        tag_id = self.find_tag_id(tag_name)
        if tag_id is None:
            LOGGER.info("Mailchimp tag was not found. tag_name=%s", tag_name)
            return []

        members = []
        list_id = quote(self.audience_id, safe="")
        segment_id = quote(str(tag_id), safe="")

        for offset in range(0, 1_000_000_000, DEFAULT_MAILCHIMP_PAGE_SIZE):
            query = urlencode(
                {
                    "count": DEFAULT_MAILCHIMP_PAGE_SIZE,
                    "offset": offset,
                }
            )
            payload = self._get_json(
                f"{self.base_url}/lists/{list_id}/segments/{segment_id}/members?{query}"
            )
            page_members = payload.get("members", [])
            if not isinstance(page_members, list):
                break

            members.extend(
                member for member in page_members if isinstance(member, dict)
            )

            total_items = int(payload.get("total_items") or 0)
            if offset + len(page_members) >= total_items or not page_members:
                break

        return members

    def find_tag_id(self, tag_name):
        list_id = quote(self.audience_id, safe="")
        query = urlencode({"name": tag_name})
        payload = self._get_json(f"{self.base_url}/lists/{list_id}/tag-search?{query}")
        tags = payload.get("tags", [])
        if not isinstance(tags, list):
            return None

        normalized_tag_name = tag_name.strip().lower()
        for tag in tags:
            if not isinstance(tag, dict):
                continue

            if str(tag.get("name", "")).strip().lower() == normalized_tag_name:
                return tag.get("id")

        return None

    def _member_url(self, subscriber_hash):
        list_id = quote(self.audience_id, safe="")
        return f"{self.base_url}/lists/{list_id}/members/{subscriber_hash}"

    def _get_json(self, url):
        response = self._request_json("GET", url)
        return json.loads(response.data.decode("utf-8"))

    def _request_json(self, method, url, body=None, allow_statuses=None):
        encoded_body = None if body is None else json.dumps(body).encode("utf-8")
        allow_statuses = allow_statuses or set()

        for attempt in range(self.max_retries + 1):
            try:
                response = self.http.request(
                    method,
                    url,
                    body=encoded_body,
                    headers=self.headers,
                    timeout=self.request_timeout,
                )
            except self.request_exception_types as error:
                if attempt >= self.max_retries:
                    raise RuntimeError(
                        f"Mailchimp request failed after {attempt + 1} attempts: {error}"
                    ) from error

                delay_seconds = self.retry_delay_seconds * (2**attempt)
                LOGGER.warning(
                    "Mailchimp request failed. Retrying request in %.2f seconds. attempt=%s max_retries=%s error=%s",
                    delay_seconds,
                    attempt + 1,
                    self.max_retries,
                    error,
                )
                self.retry_sleep(delay_seconds)
                continue

            if 200 <= response.status < 300 or response.status in allow_statuses:
                return response

            response_body = response.data.decode("utf-8", errors="replace")
            if response.status != 429 or attempt >= self.max_retries:
                raise RuntimeError(
                    f"Mailchimp request failed with status {response.status}: {response_body}"
                )

            delay_seconds = _get_retry_delay_seconds(
                response,
                self.retry_delay_seconds * (2**attempt),
            )
            LOGGER.warning(
                "Mailchimp returned 429. Retrying request in %.2f seconds. attempt=%s max_retries=%s",
                delay_seconds,
                attempt + 1,
                self.max_retries,
            )
            self.retry_sleep(delay_seconds)


def build_subscriber_hash(email):
    normalized_email = normalize_email(email)
    if not normalized_email:
        raise ValueError("Email address is required.")

    return hashlib.md5(normalized_email.encode("utf-8")).hexdigest()


def normalize_email(email):
    if email is None:
        return ""

    return str(email).strip().lower()


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


def _read_non_negative_int_env(name, default):
    raw_value = os.environ.get(name)
    if raw_value is None or raw_value == "":
        return default

    value = int(raw_value)
    if value < 0:
        raise RuntimeError(f"{name} must be greater than or equal to zero.")

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


def _get_retry_delay_seconds(response, fallback):
    retry_after = getattr(response, "headers", {}).get("Retry-After")
    if retry_after is None:
        return fallback

    try:
        return max(float(retry_after), 0.0)
    except ValueError:
        return fallback
