import csv
import io
import json
import logging
import os
from datetime import datetime, timezone
from urllib.parse import urlencode

import urllib3


LOGGER = logging.getLogger()
LOGGER.setLevel(logging.INFO)

PIANO_SEARCH_URL = "https://api.piano.io/api/v3/publisher/user/search"
DEFAULT_PIANO_AID = "28C3eb1vpu"
DEFAULT_PIANO_SOURCE = "VX"
DEFAULT_PAGE_LIMIT = 1000
DEFAULT_MAX_PAGES = 100
DEFAULT_SECRET_ID = "piano-mailchimp-webhook/production"
EXPORT_CONFIG_SECTION = "PianoSubscriberExport"


def lambda_handler(event, context):
    import boto3

    bucket_name = _read_required_env("S3_BUCKET")
    secret_id = os.environ.get("SECRET_ID", DEFAULT_SECRET_ID)
    secret_region = os.environ.get("SECRET_REGION") or os.environ.get("AWS_REGION")

    secrets_manager = boto3.client(
        "secretsmanager",
        region_name=secret_region,
    )
    config = load_secret_config(secrets_manager, secret_id)
    export_config = _get_dict(config, EXPORT_CONFIG_SECTION)
    piano_config = _get_dict(config, "Piano")

    page_limit = _read_positive_int_config(
        export_config,
        "PageLimit",
        "PAGE_LIMIT",
        DEFAULT_PAGE_LIMIT,
    )
    max_pages = _read_positive_int_config(
        export_config,
        "MaxPages",
        "MAX_PAGES",
        DEFAULT_MAX_PAGES,
    )
    api_token = _read_required_config(piano_config, "ApiToken", "Piano:ApiToken")
    aid = _read_config_value(piano_config, "ApplicationId", DEFAULT_PIANO_AID)
    source = _read_config_value(export_config, "Source", DEFAULT_PIANO_SOURCE)

    http = urllib3.PoolManager()
    users = fetch_all_users(
        http=http,
        api_token=api_token,
        aid=aid,
        source=source,
        page_limit=page_limit,
        max_pages=max_pages,
    )

    csv_body, row_count = build_subscriber_csv(users)
    key = build_export_key()

    boto3.client("s3").put_object(
        Bucket=bucket_name,
        Key=key,
        Body=csv_body.encode("utf-8"),
        ContentType="text/csv; charset=utf-8",
    )

    LOGGER.info("Uploaded %s subscriber rows to s3://%s/%s", row_count, bucket_name, key)

    return {
        "bucket": bucket_name,
        "key": key,
        "row_count": row_count,
    }


def load_secret_config(secrets_manager, secret_id):
    response = secrets_manager.get_secret_value(SecretId=secret_id)
    secret_string = response.get("SecretString")

    if not secret_string:
        raise RuntimeError(f"{secret_id} did not contain a SecretString value.")

    config = json.loads(secret_string)
    if not isinstance(config, dict):
        raise RuntimeError(f"{secret_id} SecretString must be a JSON object.")

    return config


def fetch_all_users(http, api_token, aid, source, page_limit, max_pages):
    users = []
    offset = 0

    for _ in range(max_pages):
        page = fetch_user_page(
            http=http,
            api_token=api_token,
            aid=aid,
            source=source,
            limit=page_limit,
            offset=offset,
        )

        LOGGER.info(
            "Fetched Piano user page offset=%s limit=%s row_count=%s",
            offset,
            page_limit,
            len(page),
        )

        users.extend(page)

        if len(page) < page_limit:
            break

        offset += page_limit
    else:
        LOGGER.warning("Stopped Piano pagination after MAX_PAGES=%s", max_pages)

    return users


def fetch_user_page(http, api_token, aid, source, limit, offset):
    query = urlencode(
        {
            "source": source,
            "limit": limit,
            "offset": offset,
            "order_direction": "asc",
            "has_access": "true",
            "aid": aid,
            "api_token": api_token,
        }
    )
    url = f"{PIANO_SEARCH_URL}?{query}"
    response = http.request("GET", url)

    if response.status < 200 or response.status >= 300:
        body = response.data.decode("utf-8", errors="replace")
        raise RuntimeError(
            f"Piano user search failed with status {response.status}: {body}"
        )

    payload = json.loads(response.data.decode("utf-8"))
    return extract_users(payload)


def extract_users(payload):
    if isinstance(payload, list):
        return payload

    if not isinstance(payload, dict):
        return []

    for key in ("users", "data", "items", "results"):
        value = payload.get(key)
        if isinstance(value, list):
            return value

    nested = payload.get("result")
    if isinstance(nested, dict):
        return extract_users(nested)

    return []


def build_subscriber_csv(users):
    output = io.StringIO()
    writer = csv.DictWriter(
        output,
        fieldnames=["index", "first_name", "last_name", "email"],
        lineterminator="\n",
    )
    writer.writeheader()

    row_index = 1
    for user in users:
        email = _get_text(user, "email")
        if not email:
            continue

        writer.writerow(
            {
                "index": row_index,
                "first_name": _get_text(user, "first_name"),
                "last_name": _get_text(user, "last_name"),
                "email": email,
            }
        )
        row_index += 1

    return output.getvalue(), row_index - 1


def build_export_key(now=None):
    timestamp = (now or datetime.now(timezone.utc)).strftime("%Y%m%d-%H%M%S")
    return f"piano/subscribers/subscribers-{timestamp}.csv"


def _get_text(values, key):
    if not isinstance(values, dict):
        return ""

    value = values.get(key)
    if value is None:
        return ""

    return str(value).strip()


def _read_required_env(name):
    value = os.environ.get(name)
    if not value:
        raise RuntimeError(f"{name} environment variable is required.")
    return value


def _read_positive_int_config(config, key, env_name, default):
    raw_value = config.get(key)
    if raw_value is None or raw_value == "":
        raw_value = os.environ.get(env_name)
    if raw_value is None or raw_value == "":
        return default

    value = int(raw_value)
    if value <= 0:
        raise RuntimeError(f"{EXPORT_CONFIG_SECTION}:{key} must be greater than zero.")

    return value


def _read_required_config(config, key, description):
    value = _read_config_value(config, key)
    if not value:
        raise RuntimeError(f"{description} is required in Secrets Manager config.")

    return value


def _read_config_value(config, key, default=None):
    value = config.get(key)
    if value is None or value == "":
        return default

    return str(value).strip()


def _get_dict(config, key):
    value = config.get(key)
    return value if isinstance(value, dict) else {}
