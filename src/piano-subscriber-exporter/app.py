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


def lambda_handler(event, context):
    import boto3

    page_limit = _read_positive_int("PAGE_LIMIT", DEFAULT_PAGE_LIMIT)
    max_pages = _read_positive_int("MAX_PAGES", DEFAULT_MAX_PAGES)
    bucket_name = _read_required_env("S3_BUCKET")
    api_token = _read_required_env("PIANO_API_TOKEN")
    aid = os.environ.get("PIANO_AID", DEFAULT_PIANO_AID)
    source = os.environ.get("PIANO_SOURCE", DEFAULT_PIANO_SOURCE)

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


def _read_positive_int(name, default):
    raw_value = os.environ.get(name)
    if raw_value is None or raw_value == "":
        return default

    value = int(raw_value)
    if value <= 0:
        raise RuntimeError(f"{name} must be greater than zero.")

    return value
