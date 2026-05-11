import json
import os
import sys
import types
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
        client = FakeMailchimpClient(existing_members={"ada@example.com"})

        result = app.sync_subscriber_csv(
            "\ufeffindex,first_name,last_name,email\n"
            "1,Ada,Lovelace,ada@example.com\n"
            "2,No,Email,\n"
            "3,Grace,Hopper, grace@example.com \n",
            client,
        )

        self.assertEqual(
            {
                "processed_rows": 2,
                "updated_rows": 1,
                "new_rows": 1,
                "skipped_rows": 1,
                "failed_rows": 0,
            },
            result,
        )
        self.assertEqual(
            [
                ("exists", "ada@example.com"),
                ("upsert", "ada@example.com", "Ada", "Lovelace"),
                ("tag", "ada@example.com"),
                ("exists", "grace@example.com"),
                ("upsert", "grace@example.com", "Grace", "Hopper"),
                ("tag", "grace@example.com"),
            ],
            client.calls,
        )

    def test_build_subscriber_batches_returns_valid_rows_and_skips_missing_email(self):
        rows, result = app.build_subscriber_batches(
            "\ufeffindex,first_name,last_name,email\n"
            "1,Ada,Lovelace,ada@example.com\n"
            "2,No,Email,\n"
            "3,Grace,Hopper, grace@example.com \n"
        )

        self.assertEqual({"skipped_rows": 1}, result)
        self.assertEqual(
            [
                {
                    "email": "ada@example.com",
                    "first_name": "Ada",
                    "last_name": "Lovelace",
                },
                {
                    "email": "grace@example.com",
                    "first_name": "Grace",
                    "last_name": "Hopper",
                },
            ],
            rows,
        )

    def test_chunk_rows_splits_into_fixed_size_batches(self):
        rows = [{"email": f"user-{index}@example.com"} for index in range(5)]

        batches = list(app.chunk_rows(rows, 2))

        self.assertEqual([2, 2, 1], [len(batch) for batch in batches])

    def test_worker_handler_syncs_sqs_message_batches(self):
        client = FakeMailchimpClient(existing_members={"ada@example.com"})
        original_build_mailchimp_client = app.build_mailchimp_client
        app.build_mailchimp_client = lambda: client
        try:
            result = app.worker_handler(
                {
                    "Records": [
                        {
                            "body": json.dumps(
                                {
                                    "bucket": "bucket-name",
                                    "key": "piano/subscribers/subscribers.csv",
                                    "batch_number": 1,
                                    "rows": [
                                        {
                                            "email": "ada@example.com",
                                            "first_name": "Ada",
                                            "last_name": "Lovelace",
                                        },
                                        {
                                            "email": "grace@example.com",
                                            "first_name": "Grace",
                                            "last_name": "Hopper",
                                        },
                                    ],
                                }
                            )
                        }
                    ]
                },
                None,
            )
        finally:
            app.build_mailchimp_client = original_build_mailchimp_client

        self.assertEqual(
            {
                "processed_rows": 2,
                "updated_rows": 1,
                "new_rows": 1,
                "skipped_rows": 0,
                "failed_rows": 0,
            },
            result,
        )
        self.assertEqual(
            [
                ("exists", "ada@example.com"),
                ("upsert", "ada@example.com", "Ada", "Lovelace"),
                ("tag", "ada@example.com"),
                ("exists", "grace@example.com"),
                ("upsert", "grace@example.com", "Grace", "Hopper"),
                ("tag", "grace@example.com"),
            ],
            client.calls,
        )

    def test_lambda_handler_queues_csv_batches_from_s3_event(self):
        s3 = FakeS3(
            "\ufeffindex,first_name,last_name,email\n"
            "1,Ada,Lovelace,ada@example.com\n"
            "2,Grace,Hopper,grace@example.com\n"
            "3,Katherine,Johnson,katherine@example.com\n"
        )
        sqs = FakeSqs()
        original_boto3 = sys.modules.get("boto3")
        sys.modules["boto3"] = types.SimpleNamespace(
            client=lambda name, **_: {"s3": s3, "sqs": sqs}[name]
        )
        original_queue_url = os.environ.get("SYNC_QUEUE_URL")
        original_batch_size = os.environ.get("SYNC_BATCH_SIZE")
        os.environ["SYNC_QUEUE_URL"] = "https://sqs.example.test/queue"
        os.environ["SYNC_BATCH_SIZE"] = "2"
        client = FakeMailchimpClient(
            paid_members=[
                {"email_address": "ada@example.com"},
                {"email_address": "expired@example.com"},
            ]
        )
        original_build_mailchimp_client = app.build_mailchimp_client
        app.build_mailchimp_client = lambda: client

        try:
            result = app.lambda_handler(
                {
                    "Records": [
                        {
                            "s3": {
                                "bucket": {"name": "bucket-name"},
                                "object": {
                                    "key": "piano/subscribers/subscribers.csv",
                                },
                            }
                        }
                    ]
                },
                None,
            )
        finally:
            if original_boto3 is None:
                del sys.modules["boto3"]
            else:
                sys.modules["boto3"] = original_boto3

            if original_queue_url is None:
                os.environ.pop("SYNC_QUEUE_URL", None)
            else:
                os.environ["SYNC_QUEUE_URL"] = original_queue_url

            if original_batch_size is None:
                os.environ.pop("SYNC_BATCH_SIZE", None)
            else:
                os.environ["SYNC_BATCH_SIZE"] = original_batch_size

            app.build_mailchimp_client = original_build_mailchimp_client

        self.assertEqual(
            {
                "queued_batches": 2,
                "queued_rows": 3,
                "skipped_rows": 0,
                "reconciliation": {
                    "active_emails": 3,
                    "paid_members": 2,
                    "expired_members": 1,
                    "failed_members": 0,
                    "skipped_blank_members": 0,
                },
            },
            result,
        )
        self.assertEqual(
            [
                "https://sqs.example.test/queue",
                "https://sqs.example.test/queue",
            ],
            [message["QueueUrl"] for message in sqs.messages],
        )
        self.assertEqual(
            [2, 1],
            [len(json.loads(message["MessageBody"])["rows"]) for message in sqs.messages],
        )
        self.assertIn(("expired", "expired@example.com"), client.calls)

    def test_lambda_handler_reconciles_before_queueing_csv_batches(self):
        s3 = FakeS3(
            "\ufeffindex,first_name,last_name,email\n"
            "1,Ada,Lovelace,ada@example.com\n"
            "2,Grace,Hopper,grace@example.com\n"
        )
        sqs = FakeSqs()
        original_boto3 = sys.modules.get("boto3")
        sys.modules["boto3"] = types.SimpleNamespace(
            client=lambda name, **_: {"s3": s3, "sqs": sqs}[name]
        )
        original_queue_url = os.environ.get("SYNC_QUEUE_URL")
        original_batch_size = os.environ.get("SYNC_BATCH_SIZE")
        os.environ["SYNC_QUEUE_URL"] = "https://sqs.example.test/queue"
        os.environ["SYNC_BATCH_SIZE"] = "1"
        client = FakeMailchimpClient()
        original_build_mailchimp_client = app.build_mailchimp_client
        app.build_mailchimp_client = lambda: client

        try:
            app.lambda_handler(
                {
                    "Records": [
                        {
                            "s3": {
                                "bucket": {"name": "bucket-name"},
                                "object": {
                                    "key": "piano/subscribers/subscribers.csv",
                                },
                            }
                        }
                    ]
                },
                None,
            )
        finally:
            if original_boto3 is None:
                del sys.modules["boto3"]
            else:
                sys.modules["boto3"] = original_boto3

            if original_queue_url is None:
                os.environ.pop("SYNC_QUEUE_URL", None)
            else:
                os.environ["SYNC_QUEUE_URL"] = original_queue_url

            if original_batch_size is None:
                os.environ.pop("SYNC_BATCH_SIZE", None)
            else:
                os.environ["SYNC_BATCH_SIZE"] = original_batch_size

            app.build_mailchimp_client = original_build_mailchimp_client

        self.assertEqual(("list_tagged", "PAID"), client.calls[0])
        self.assertEqual(2, len(sqs.messages))

    def test_lambda_handler_does_not_queue_batches_when_reconciliation_fails(self):
        s3 = FakeS3(
            "\ufeffindex,first_name,last_name,email\n"
            "1,Ada,Lovelace,ada@example.com\n"
            "2,Grace,Hopper,grace@example.com\n"
        )
        sqs = FakeSqs()
        original_boto3 = sys.modules.get("boto3")
        sys.modules["boto3"] = types.SimpleNamespace(
            client=lambda name, **_: {"s3": s3, "sqs": sqs}[name]
        )
        original_queue_url = os.environ.get("SYNC_QUEUE_URL")
        os.environ["SYNC_QUEUE_URL"] = "https://sqs.example.test/queue"
        client = FakeMailchimpClient(fail_list_tagged=True)
        original_build_mailchimp_client = app.build_mailchimp_client
        app.build_mailchimp_client = lambda: client

        try:
            with self.assertRaisesRegex(RuntimeError, "Mailchimp list failed"):
                app.lambda_handler(
                    {
                        "Records": [
                            {
                                "s3": {
                                    "bucket": {"name": "bucket-name"},
                                    "object": {
                                        "key": "piano/subscribers/subscribers.csv",
                                    },
                                }
                            }
                        ]
                    },
                    None,
                )
        finally:
            if original_boto3 is None:
                del sys.modules["boto3"]
            else:
                sys.modules["boto3"] = original_boto3

            if original_queue_url is None:
                os.environ.pop("SYNC_QUEUE_URL", None)
            else:
                os.environ["SYNC_QUEUE_URL"] = original_queue_url

            app.build_mailchimp_client = original_build_mailchimp_client

        self.assertEqual([], sqs.messages)

    def test_sync_subscriber_rows_continues_after_mailchimp_row_failure(self):
        client = FakeMailchimpClient(failing_emails={"ada@example.com"})

        result = app.sync_subscriber_rows(
            [
                {
                    "email": "ada@example.com",
                    "first_name": "Ada",
                    "last_name": "Lovelace",
                },
                {
                    "email": "grace@example.com",
                    "first_name": "Grace",
                    "last_name": "Hopper",
                },
            ],
            client,
        )

        self.assertEqual(
            {
                "processed_rows": 1,
                "updated_rows": 0,
                "new_rows": 1,
                "skipped_rows": 0,
                "failed_rows": 1,
            },
            result,
        )
        self.assertIn(("upsert", "grace@example.com", "Grace", "Hopper"), client.calls)

    def test_reconcile_expired_paid_subscribers_uses_normalized_email_match(self):
        client = FakeMailchimpClient(
            paid_members=[
                {"email_address": " ADA@example.com "},
                {"email_address": "expired@example.com"},
                {"email_address": ""},
            ]
        )

        result = app.reconcile_expired_paid_subscribers({"ada@example.com"}, client)

        self.assertEqual(
            {
                "active_emails": 1,
                "paid_members": 3,
                "expired_members": 1,
                "failed_members": 0,
                "skipped_blank_members": 1,
            },
            result,
        )
        self.assertEqual(
            [("list_tagged", "PAID"), ("expired", "expired@example.com")],
            client.calls,
        )

    def test_mailchimp_client_uses_upsert_endpoint_without_status_update(self):
        http = FakeHttp()
        client = app.MailchimpClient(
            http=http,
            api_key="api-key",
            server_prefix="us1",
            audience_id="audience-id",
            request_timeout="timeout",
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
        self.assertEqual("timeout", request["timeout"])

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

    def test_mailchimp_client_adds_expired_tag_without_removing_existing_tags(self):
        http = FakeHttp()
        client = app.MailchimpClient(
            http=http,
            api_key="api-key",
            server_prefix="us1",
            audience_id="audience-id",
        )

        client.ensure_expired_tag("expired@example.com")

        request = http.requests[0]
        body = json.loads(request["body"].decode("utf-8"))

        self.assertEqual("POST", request["method"])
        self.assertEqual(
            {
                "tags": [
                    {
                        "name": "EXPIRED",
                        "status": "active",
                    }
                ]
            },
            body,
        )

    def test_mailchimp_client_checks_whether_member_exists(self):
        http = FakeHttp([FakeResponse(200), FakeResponse(404)])
        client = app.MailchimpClient(
            http=http,
            api_key="api-key",
            server_prefix="us1",
            audience_id="audience-id",
        )

        self.assertTrue(client.member_exists("ada@example.com"))
        self.assertFalse(client.member_exists("grace@example.com"))

        self.assertEqual(["GET", "GET"], [request["method"] for request in http.requests])

    def test_mailchimp_client_lists_paid_tagged_members(self):
        http = FakeHttp(
            [
                FakeResponse(
                    200,
                    data={
                        "tags": [
                            {
                                "id": 123,
                                "name": "PAID",
                            }
                        ]
                    },
                ),
                FakeResponse(
                    200,
                    data={
                        "members": [
                            {"email_address": "ada@example.com"},
                            {"email_address": "expired@example.com"},
                        ],
                        "total_items": 2,
                    },
                ),
            ]
        )
        client = app.MailchimpClient(
            http=http,
            api_key="api-key",
            server_prefix="us1",
            audience_id="audience-id",
        )

        members = client.list_tagged_members("PAID")

        self.assertEqual(
            [
                {"email_address": "ada@example.com"},
                {"email_address": "expired@example.com"},
            ],
            members,
        )
        self.assertEqual(
            "https://us1.api.mailchimp.com/3.0/lists/audience-id/tag-search?name=PAID",
            http.requests[0]["url"],
        )
        self.assertEqual(
            "https://us1.api.mailchimp.com/3.0/lists/audience-id/segments/123/members?count=1000&offset=0",
            http.requests[1]["url"],
        )

    def test_mailchimp_client_retries_429_response(self):
        http = FakeHttp([FakeResponse(429), FakeResponse(200)])
        sleep_calls = []
        client = app.MailchimpClient(
            http=http,
            api_key="api-key",
            server_prefix="us1",
            audience_id="audience-id",
            retry_sleep=sleep_calls.append,
        )

        client.upsert_member("ada@example.com", "Ada", "Lovelace")

        self.assertEqual(2, len(http.requests))
        self.assertEqual([1.0], sleep_calls)

    def test_mailchimp_client_uses_retry_after_header_for_429_response(self):
        http = FakeHttp(
            [FakeResponse(429, headers={"Retry-After": "2.5"}), FakeResponse(200)]
        )
        sleep_calls = []
        client = app.MailchimpClient(
            http=http,
            api_key="api-key",
            server_prefix="us1",
            audience_id="audience-id",
            retry_sleep=sleep_calls.append,
        )

        client.upsert_member("ada@example.com", "Ada", "Lovelace")

        self.assertEqual([2.5], sleep_calls)

    def test_mailchimp_client_raises_after_429_retries_are_exhausted(self):
        http = FakeHttp([FakeResponse(429), FakeResponse(429)])
        client = app.MailchimpClient(
            http=http,
            api_key="api-key",
            server_prefix="us1",
            audience_id="audience-id",
            max_retries=1,
            retry_sleep=lambda _: None,
        )

        with self.assertRaisesRegex(RuntimeError, "Mailchimp request failed with status 429"):
            client.upsert_member("ada@example.com", "Ada", "Lovelace")

        self.assertEqual(2, len(http.requests))

    def test_mailchimp_client_retries_request_exceptions(self):
        http = FakeHttp([FakeMailchimpTimeout("read timed out"), FakeResponse(200)])
        sleep_calls = []
        client = app.MailchimpClient(
            http=http,
            api_key="api-key",
            server_prefix="us1",
            audience_id="audience-id",
            retry_sleep=sleep_calls.append,
            request_exception_types=(FakeMailchimpTimeout,),
        )

        client.upsert_member("ada@example.com", "Ada", "Lovelace")

        self.assertEqual(2, len(http.requests))
        self.assertEqual([1.0], sleep_calls)

    def test_mailchimp_client_raises_after_request_exception_retries_are_exhausted(self):
        http = FakeHttp(
            [
                FakeMailchimpTimeout("first timeout"),
                FakeMailchimpTimeout("second timeout"),
            ]
        )
        client = app.MailchimpClient(
            http=http,
            api_key="api-key",
            server_prefix="us1",
            audience_id="audience-id",
            max_retries=1,
            retry_sleep=lambda _: None,
            request_exception_types=(FakeMailchimpTimeout,),
        )

        with self.assertRaisesRegex(
            RuntimeError,
            "Mailchimp request failed after 2 attempts: second timeout",
        ):
            client.upsert_member("ada@example.com", "Ada", "Lovelace")

        self.assertEqual(2, len(http.requests))


class FakeMailchimpClient:
    def __init__(
        self,
        existing_members=None,
        paid_members=None,
        failing_emails=None,
        fail_list_tagged=False,
    ):
        self.calls = []
        self.existing_members = set(existing_members or [])
        self.paid_members = list(paid_members or [])
        self.failing_emails = set(failing_emails or [])
        self.fail_list_tagged = fail_list_tagged

    def member_exists(self, email):
        self.calls.append(("exists", email))
        return email in self.existing_members

    def upsert_member(self, email, first_name, last_name):
        self.calls.append(("upsert", email, first_name, last_name))
        if email in self.failing_emails:
            raise RuntimeError("Mailchimp upsert failed.")

    def ensure_paid_tag(self, email):
        self.calls.append(("tag", email))

    def ensure_expired_tag(self, email):
        self.calls.append(("expired", email))

    def list_tagged_members(self, tag_name):
        self.calls.append(("list_tagged", tag_name))
        if self.fail_list_tagged:
            raise RuntimeError("Mailchimp list failed.")

        return self.paid_members


class FakeHttp:
    def __init__(self, responses=None):
        self.requests = []
        self.responses = list(responses or [FakeResponse(200)])

    def request(self, method, url, body, headers, timeout=None):
        self.requests.append(
            {
                "method": method,
                "url": url,
                "body": body,
                "headers": headers,
                "timeout": timeout,
            }
        )
        if len(self.responses) > 1:
            response = self.responses.pop(0)
        else:
            response = self.responses[0]

        if isinstance(response, BaseException):
            raise response

        return response


class FakeMailchimpTimeout(Exception):
    pass


class FakeS3:
    def __init__(self, body):
        self.body = body

    def get_object(self, Bucket, Key):
        return {
            "Body": FakeBody(self.body.encode("utf-8")),
        }


class FakeBody:
    def __init__(self, body):
        self.body = body

    def read(self):
        return self.body


class FakeSqs:
    def __init__(self):
        self.messages = []

    def send_message(self, QueueUrl, MessageBody):
        self.messages.append(
            {
                "QueueUrl": QueueUrl,
                "MessageBody": MessageBody,
            }
        )


class FakeResponse:
    def __init__(self, status, headers=None, data=None):
        self.status = status
        self.headers = headers or {}
        self.data = json.dumps(data or {}).encode("utf-8")


if __name__ == "__main__":
    unittest.main()
