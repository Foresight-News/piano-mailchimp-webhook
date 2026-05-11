import csv
import io
import json
import sys
import unittest
from datetime import datetime, timezone
from pathlib import Path


APP_DIR = Path(__file__).resolve().parents[2] / "src" / "piano-subscriber-exporter"
sys.path.insert(0, str(APP_DIR))

import app


class PianoSubscriberExporterTests(unittest.TestCase):
    def test_fetch_all_users_uses_offset_pagination_until_short_page(self):
        http = FakeHttp(
            [
                {"users": [{"email": "one@example.com"}, {"email": "two@example.com"}]},
                {"users": [{"email": "three@example.com"}]},
            ]
        )

        users = app.fetch_all_users(
            http=http,
            api_token="token",
            aid="aid",
            source="VX",
            page_limit=2,
            max_pages=10,
        )

        self.assertEqual(3, len(users))
        self.assertIn("limit=2", http.urls[0])
        self.assertIn("offset=0", http.urls[0])
        self.assertIn("limit=2", http.urls[1])
        self.assertIn("offset=2", http.urls[1])

    def test_fetch_all_users_stops_at_max_pages(self):
        http = FakeHttp(
            [
                {"users": [{"email": "one@example.com"}]},
                {"users": [{"email": "two@example.com"}]},
            ]
        )

        users = app.fetch_all_users(
            http=http,
            api_token="token",
            aid="aid",
            source="VX",
            page_limit=1,
            max_pages=2,
        )

        self.assertEqual(2, len(users))
        self.assertEqual(2, len(http.urls))

    def test_build_subscriber_csv_outputs_only_required_columns_and_skips_missing_email(self):
        csv_body, row_count = app.build_subscriber_csv(
            [
                {
                    "first_name": "Ada",
                    "last_name": "Lovelace",
                    "email": "ada@example.com",
                    "uid": "not-exported",
                },
                {
                    "first_name": "No",
                    "last_name": "Email",
                    "email": "",
                },
                {
                    "first_name": "Grace",
                    "last_name": "Hopper",
                    "email": " grace@example.com ",
                },
            ]
        )

        rows = list(csv.DictReader(io.StringIO(csv_body)))

        self.assertEqual(2, row_count)
        self.assertEqual(["index", "first_name", "last_name", "email"], list(rows[0].keys()))
        self.assertEqual("1", rows[0]["index"])
        self.assertEqual("ada@example.com", rows[0]["email"])
        self.assertEqual("2", rows[1]["index"])
        self.assertEqual("grace@example.com", rows[1]["email"])

    def test_build_export_key_uses_required_path_and_timestamp_format(self):
        key = app.build_export_key(datetime(2026, 5, 11, 12, 34, 56, tzinfo=timezone.utc))

        self.assertEqual("piano/subscribers/subscribers-20260511-123456.csv", key)


class FakeHttp:
    def __init__(self, payloads):
        self.payloads = list(payloads)
        self.urls = []

    def request(self, method, url):
        self.urls.append(url)
        return FakeResponse(200, self.payloads.pop(0))


class FakeResponse:
    def __init__(self, status, payload):
        self.status = status
        self.data = json.dumps(payload).encode("utf-8")


if __name__ == "__main__":
    unittest.main()
