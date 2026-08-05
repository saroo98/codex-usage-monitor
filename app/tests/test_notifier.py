from __future__ import annotations

import json
import os
import sys
import tempfile
import time
import unittest
from contextlib import contextmanager
from pathlib import Path
from unittest import mock

import codex_usage_notifier as n


@contextmanager
def temporary_data_paths():
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        with mock.patch.multiple(
            n,
            DATA_DIR=root,
            CONFIG_FILE=root / "config.json",
            STATE_FILE=root / "state.json",
            STATE_BACKUP_FILE=root / "state.backup.json",
            HEARTBEAT_FILE=root / "heartbeat.json",
            LOG_FILE=root / "monitor.log",
            APP_SERVER_LOG_FILE=root / "app-server.log",
            LOCK_FILE=root / "monitor.lock",
        ):
            yield root


def config(**updates):
    value = n.deep_merge(n.DEFAULT_CONFIG, updates)
    value["freshness_probe_delay_seconds"] = updates.get("freshness_probe_delay_seconds", 0)
    value["confirmation_delay_seconds"] = updates.get("confirmation_delay_seconds", 0)
    return n.validate_config(value)


def meter(
    key="codex:primary",
    remaining=0.0,
    *,
    used=None,
    reset=1_900_000_000,
    duration=300,
    reached=None,
    limit_id="codex",
    limit_name=None,
    slot="primary",
):
    if used is None:
        used = 100.0 - remaining
    return n.Meter(
        key=key,
        limit_id=limit_id,
        limit_name=limit_name,
        slot=slot,
        used_percent=float(used),
        remaining_percent=float(remaining),
        window_duration_mins=duration,
        resets_at=reset,
        reached_type=reached,
        plan_type="plus",
    )


def snapshot(meters=None, credits=0):
    values = meters or {"codex:primary": meter()}
    return n.Snapshot(
        fetched_at=n.utc_now_iso(),
        account_type="chatgpt",
        plan_type="plus",
        meters=values,
        reset_credit_count=credits,
        credit_balance=None,
        unlimited_credits=False,
    )


class NormalizationTests(unittest.TestCase):
    def test_primary_and_secondary(self):
        result = {
            "rateLimitsByLimitId": {
                "codex": {
                    "limitId": "codex",
                    "primary": {"usedPercent": 90, "windowDurationMins": 300, "resetsAt": 1000},
                    "secondary": {"usedPercent": 25, "windowDurationMins": 10080, "resetsAt": 2000},
                    "rateLimitReachedType": None,
                }
            },
            "rateLimitResetCredits": {"availableCount": 2},
        }
        value = n.normalize_snapshot({"type": "chatgpt", "planType": "plus"}, result, config())
        self.assertEqual(value.meters["codex:primary"].remaining_percent, 10)
        self.assertEqual(value.meters["codex:secondary"].remaining_percent, 75)
        self.assertEqual(value.reset_credit_count, 2)

    def test_snake_case_fields(self):
        result = {
            "rate_limits": {
                "limit_id": "codex",
                "primary": {"used_percent": 20, "window_duration_mins": 60, "resets_at": 1000},
                "secondary": None,
            }
        }
        value = n.normalize_snapshot({"type": "chatgpt", "plan_type": "pro"}, result, config())
        self.assertEqual(value.meters["codex:primary"].remaining_percent, 80)
        self.assertEqual(value.plan_type, "pro")

    def test_filter_limit_ids(self):
        result = {
            "rateLimitsByLimitId": {
                "a": {"limitId": "a", "primary": {"usedPercent": 10}},
                "b": {"limitId": "b", "primary": {"usedPercent": 20}},
            }
        }
        value = n.normalize_snapshot(
            {"type": "chatgpt"}, result, config(monitor_limit_ids=["b"])
        )
        self.assertEqual(set(value.meters), {"b:primary"})

    def test_bucket_plan_fills_account_plan(self):
        result = {
            "rateLimits": {
                "limitId": "codex",
                "planType": "business",
                "primary": {"usedPercent": 10},
            }
        }
        value = n.normalize_snapshot({"type": "chatgpt"}, result, config())
        self.assertEqual(value.plan_type, "business")

    def test_empty_response_rejected(self):
        with self.assertRaises(n.AppServerError):
            n.normalize_snapshot({"type": "chatgpt"}, {}, config())

    def test_external_auth_tokens_rejected(self):
        class Client:
            def request(self, method, params=None):
                return {"account": {"type": "chatgptAuthTokens"}}

        with self.assertRaises(n.AuthenticationError):
            n.check_account(Client(), False)

    def test_configuration_requires_visible_channel(self):
        bad = n.deep_merge(
            n.DEFAULT_CONFIG,
            {"notification": {"toast": False, "tray_balloon": False, "popup": False}},
        )
        with self.assertRaises(n.NotifierError):
            n.validate_config(bad)

    def test_initialize_config_migrates_older_schema(self):
        with temporary_data_paths():
            n.atomic_write_json(
                n.CONFIG_FILE,
                {
                    "schema_version": 3,
                    "poll_seconds": 75,
                    "notification": {"popup": True},
                },
            )
            value = n.initialize_config("codex-custom")
            self.assertEqual(value["schema_version"], 5)
            self.assertEqual(value["poll_seconds"], 75)
            self.assertEqual(value["codex_command"], "codex-custom")
            self.assertEqual(value["ui"]["live_widget"], True)
            self.assertEqual(value["ui"]["preferred_meter"], "auto")
            self.assertEqual(json.loads(n.CONFIG_FILE.read_text())["schema_version"], 5)

    def test_ui_configuration_defaults_are_valid(self):
        value = n.validate_config(n.DEFAULT_CONFIG)
        self.assertEqual(value["schema_version"], 5)
        self.assertTrue(value["ui"]["live_widget"])
        self.assertTrue(value["ui"]["always_on_top"])
        self.assertEqual(value["ui"]["preferred_meter"], "auto")
        self.assertEqual(value["ui"]["stale_after_seconds"], 180)
        self.assertEqual(value["ui"]["refresh_milliseconds"], 1000)
        self.assertTrue(value["usage_url"].startswith("https://"))

    def test_ui_refresh_interval_is_bounded(self):
        too_fast = n.deep_merge(
            n.DEFAULT_CONFIG, {"ui": {"refresh_milliseconds": 499}}
        )
        too_slow = n.deep_merge(
            n.DEFAULT_CONFIG, {"ui": {"refresh_milliseconds": 5001}}
        )
        with self.assertRaises(n.NotifierError):
            n.validate_config(too_fast)
        with self.assertRaises(n.NotifierError):
            n.validate_config(too_slow)


class EventTests(unittest.TestCase):
    def test_any_increase_triggers(self):
        before = snapshot({"codex:primary": meter(remaining=0)})
        after = snapshot({"codex:primary": meter(remaining=0.5)})
        self.assertEqual(len(n.detect_events(before, after, config())), 1)

    def test_threshold_crossing_triggers_when_any_increase_disabled(self):
        before = snapshot({"codex:primary": meter(remaining=1)})
        after = snapshot({"codex:primary": meter(remaining=2)})
        events = n.detect_events(before, after, config(notify_on_any_increase=False))
        self.assertEqual(len(events), 1)

    def test_decrease_does_not_trigger(self):
        before = snapshot({"codex:primary": meter(remaining=50)})
        after = snapshot({"codex:primary": meter(remaining=49)})
        self.assertEqual(n.detect_events(before, after, config()), [])

    def test_reached_state_cleared(self):
        before = snapshot({"codex:primary": meter(remaining=0, reached="primary")})
        after = snapshot({"codex:primary": meter(remaining=0, reached=None)})
        events = n.detect_events(before, after, config(notify_on_any_increase=False))
        self.assertEqual(len(events), 1)

    def test_new_reset_credit_triggers(self):
        before = snapshot(credits=0)
        after = snapshot(credits=2)
        events = n.detect_events(before, after, config(notify_on_any_increase=False))
        self.assertEqual(events[0].kind, "reset_credit")

    def test_renamed_limit_id_matches_unique_window(self):
        before_meter = meter(key="old:primary", limit_id="old", remaining=0)
        after_meter = meter(key="new:primary", limit_id="new", remaining=10)
        events = n.detect_events(
            snapshot({before_meter.key: before_meter}),
            snapshot({after_meter.key: after_meter}),
            config(),
        )
        self.assertEqual(len(events), 1)

    def test_new_meter_without_baseline_does_not_trigger(self):
        before = snapshot({"codex:primary": meter(remaining=0)})
        new = meter(key="other:primary", limit_id="other", remaining=100, duration=60)
        after = snapshot({"codex:primary": meter(remaining=0), new.key: new})
        self.assertEqual(n.detect_events(before, after, config()), [])


class PersistenceTests(unittest.TestCase):
    def test_atomic_json_roundtrip(self):
        with temporary_data_paths() as root:
            path = root / "value.json"
            n.atomic_write_json(path, {"a": 1})
            self.assertEqual(json.loads(path.read_text()), {"a": 1})

    def test_redundant_state_recovery(self):
        with temporary_data_paths():
            good = n.default_state()
            good["consecutive_failures"] = 3
            n.atomic_write_json(n.STATE_BACKUP_FILE, good)
            n.STATE_FILE.write_text("{bad", encoding="utf-8")
            self.assertEqual(n.load_state()["consecutive_failures"], 3)

    def test_newer_state_copy_is_selected(self):
        with temporary_data_paths():
            old = n.default_state()
            old["updated_at"] = "2026-01-01T00:00:00+00:00"
            old["consecutive_failures"] = 1
            new = n.default_state()
            new["updated_at"] = "2026-01-02T00:00:00+00:00"
            new["consecutive_failures"] = 4
            n.atomic_write_json(n.STATE_FILE, old)
            n.atomic_write_json(n.STATE_BACKUP_FILE, new)
            self.assertEqual(n.load_state()["consecutive_failures"], 4)

    def test_missing_state_recovers_heartbeat_baseline(self):
        with temporary_data_paths():
            baseline = snapshot({"codex:primary": meter(remaining=12)})
            n.atomic_write_json(
                n.HEARTBEAT_FILE,
                {"checked_at": n.utc_now_iso(), "status": "ok", "snapshot": baseline.as_dict()},
            )
            loaded = n.load_state()
            recovered = n.snapshot_from_state(loaded["last_snapshot"])
            self.assertEqual(recovered.meters["codex:primary"].remaining_percent, 12)

    def test_pending_alert_is_not_overridden_by_newer_heartbeat(self):
        with temporary_data_paths():
            old = snapshot({"codex:primary": meter(remaining=0)})
            newer = snapshot({"codex:primary": meter(remaining=100)})
            state = n.default_state()
            state["updated_at"] = "2026-01-01T00:00:00+00:00"
            state["last_snapshot"] = old.as_dict()
            state["pending_alert"] = {"title": "pending"}
            n.atomic_write_json(n.STATE_FILE, state)
            n.atomic_write_json(
                n.HEARTBEAT_FILE,
                {"checked_at": "2026-01-02T00:00:00+00:00", "status": "ok", "snapshot": newer.as_dict()},
            )
            loaded = n.load_state()
            recovered = n.snapshot_from_state(loaded["last_snapshot"])
            self.assertEqual(recovered.meters["codex:primary"].remaining_percent, 0)

    def test_regressed_reset_window_is_rejected(self):
        before = snapshot({"codex:primary": meter(remaining=0, reset=2000)})
        after = snapshot({"codex:primary": meter(remaining=100, reset=1000)})
        result = n.discard_regressed_windows(before, after)
        self.assertEqual(result.meters["codex:primary"].remaining_percent, 0)


class ReliabilityTests(unittest.TestCase):
    def test_two_confirmed_reads_alert_once(self):
        with temporary_data_paths():
            previous = snapshot({"codex:primary": meter(remaining=0)})
            current = snapshot({"codex:primary": meter(remaining=100)})
            state = n.default_state()
            state["last_snapshot"] = previous.as_dict()
            with mock.patch.object(n, "send_desktop_alert", return_value=True) as send:
                final, events = n.process_snapshot(
                    state, current, config(), confirm_fetch=lambda: current
                )
            self.assertEqual(final.meters["codex:primary"].remaining_percent, 100)
            self.assertEqual(len(events), 1)
            self.assertEqual(send.call_count, 1)
            self.assertIsNone(state["pending_alert"])

    def test_unconfirmed_spike_is_discarded(self):
        with temporary_data_paths():
            previous = snapshot({"codex:primary": meter(remaining=0)})
            spike = snapshot({"codex:primary": meter(remaining=100)})
            normal = snapshot({"codex:primary": meter(remaining=0)})
            state = n.default_state()
            state["last_snapshot"] = previous.as_dict()
            samples = iter([normal, normal])
            with mock.patch.object(n, "send_desktop_alert", return_value=True) as send:
                _, events = n.process_snapshot(
                    state,
                    spike,
                    config(maximum_confirmation_reads=3),
                    confirm_fetch=lambda: next(samples),
                )
            self.assertEqual(events, [])
            send.assert_not_called()

    def test_pending_alert_retries_before_advancing_state(self):
        with temporary_data_paths():
            current = snapshot({"codex:primary": meter(remaining=100)})
            state = n.default_state()
            state["pending_alert"] = {
                "title": "Title",
                "message": "Message",
                "snapshot": current.as_dict(),
                "fingerprint": "abc",
                "created_at": n.utc_now_iso(),
            }
            with mock.patch.object(n, "send_desktop_alert", return_value=True):
                self.assertTrue(n.retry_pending_alert(state, config()))
            self.assertIsNone(state["pending_alert"])
            self.assertEqual(
                n.snapshot_from_state(state["last_snapshot"]).meters["codex:primary"].remaining_percent,
                100,
            )

    def test_poll_accelerates_after_expected_reset(self):
        expired = time.time() - 10
        value = snapshot({"codex:primary": meter(remaining=0, reset=expired)})
        with mock.patch.object(n.random, "uniform", return_value=0):
            self.assertEqual(n.seconds_until_next_check(value, config()), 10)


class AppServerIntegrationTests(unittest.TestCase):
    def fixture(self, name):
        return str(Path(__file__).with_name(name))

    def test_python_fixture_invocation_uses_current_interpreter(self):
        invocation = n.build_codex_invocation(self.fixture("fake_codex_server.py"), "app-server")
        self.assertEqual(invocation[0], sys.executable)

    def test_fake_app_server_round_trip(self):
        with temporary_data_paths():
            with n.AppServerClient(self.fixture("fake_codex_server.py"), 5) as client:
                account = n.check_account(client, True)
                value = n.fetch_snapshot(client, config())
            self.assertEqual(account["type"], "chatgpt")
            self.assertEqual(len(value.meters), 2)
            self.assertEqual(value.reset_credit_count, 1)

    def test_freshness_probe_uses_second_read(self):
        with temporary_data_paths():
            cfg = config(freshness_probe_delay_seconds=0.01)
            with n.AppServerClient(self.fixture("fake_codex_stale_server.py"), 5) as client:
                value = n.fetch_fresh_snapshot(client, cfg)
            self.assertEqual(value.meters["codex:primary"].remaining_percent, 50)

    def test_string_id_callback_is_rejected_without_deadlock(self):
        with temporary_data_paths():
            with n.AppServerClient(self.fixture("fake_codex_callback_server.py"), 5) as client:
                value = n.fetch_snapshot(client, config())
            self.assertEqual(value.meters["codex:primary"].remaining_percent, 75)


if __name__ == "__main__":
    unittest.main()
