import json
import sys
import unittest
from pathlib import Path


APP_DIR = Path(__file__).resolve().parents[2] / "src" / "piano-subscriber-mailchimp-syncer"
sys.path.insert(0, str(APP_DIR))

import app


class PianoSubscriberMailchimpSyncerTests(unittest.TestCase):
    def test_build_subscriber_hash_uses_lowercase_trimmed_email_md5(self):
        self.assertEqual(
            "3e3417d7ef77d5932a6734b916515ed5",
            app.build_subscriber_hash(" Ada@Example.COM "),
        )

    def test_sync_subscriber_csv_upserts_rows_and_skips_missing_email(self):
        client = FakeMailchimpClient()

        result = app.sync_subscriber_csv(
            "\ufeffindex,first_name,last_name,email\n"
            "1,Ada,Lovelace,ada@example.com\n"
            "2,No,Email,\n"
            "3,Grace,Hopper, grace@example.com \n",
            client,
        )

        self.assertEqual({"processed_rows": 2, "skipped_rows": 1}, result)
        self.assertEqual(
            [
                ("upsert", "ada@example.com", "Ada", "Lovelace"),
                ("tag", "ada@example.com"),
                ("upsert", "grace@example.com", "Grace", "Hopper"),
                ("tag", "grace@example.com"),
            ],
            client.calls,
        )

    def test_mailchimp_client_uses_upsert_endpoint_without_status_update(self):
        http = FakeHttp()
        client = app.MailchimpClient(
            http=http,
            api_key="api-key",
            server_prefix="us1",
            audience_id="audience-id",
        )

        client.upsert_member("Ada@Example.COM", "Ada", "Lovelace")

        request = http.requests[0]
        body = json.loads(request["body"].decode("utf-8"))

        self.assertEqual("PUT", request["method"])
        self.assertEqual(
            "https://us1.api.mailchimp.com/3.0/lists/audience-id/members/3e3417d7ef77d5932a6734b916515ed5",
            request["url"],
        )
        self.assertEqual("Ada@Example.COM", body["email_address"])
        self.assertEqual("subscribed", body["status_if_new"])
        self.assertNotIn("status", body)
        self.assertEqual({"FNAME": "Ada", "LNAME": "Lovelace"}, body["merge_fields"])

    def test_mailchimp_client_adds_paid_tag_without_removing_existing_tags(self):
        http = FakeHttp()
        client = app.MailchimpClient(
            http=http,
            api_key="api-key",
            server_prefix="us1",
            audience_id="audience-id",
        )

        client.ensure_paid_tag("ada@example.com")

        request = http.requests[0]
        body = json.loads(request["body"].decode("utf-8"))

        self.assertEqual("POST", request["method"])
        self.assertEqual(
            "https://us1.api.mailchimp.com/3.0/lists/audience-id/members/3e3417d7ef77d5932a6734b916515ed5/tags",
            request["url"],
        )
        self.assertEqual({"tags": [{"name": "PAID", "status": "active"}]}, body)


class FakeMailchimpClient:
    def __init__(self):
        self.calls = []

    def upsert_member(self, email, first_name, last_name):
        self.calls.append(("upsert", email, first_name, last_name))

    def ensure_paid_tag(self, email):
        self.calls.append(("tag", email))


class FakeHttp:
    def __init__(self):
        self.requests = []

    def request(self, method, url, body, headers):
        self.requests.append(
            {
                "method": method,
                "url": url,
                "body": body,
                "headers": headers,
            }
        )
        return FakeResponse(200)


class FakeResponse:
    def __init__(self, status):
        self.status = status
        self.data = b"{}"


if __name__ == "__main__":
    unittest.main()
