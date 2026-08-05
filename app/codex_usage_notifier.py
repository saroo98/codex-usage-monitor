#!/usr/bin/env python3
"""Codex Usage Notifier for Windows.

Reads ChatGPT-backed Codex rate limits from the documented Codex app-server
JSON-RPC interface and displays a local desktop alert when remaining capacity
increases or becomes available again.
"""

from __future__ import annotations

import argparse
import contextlib
import dataclasses
import hashlib
import json
import logging
import logging.handlers
import os
import platform
import queue
import random
import shlex
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Iterable, Mapping, MutableMapping, Sequence

APP_NAME = "Codex Usage Notifier"
APP_SLUG = "CodexUsageNotifier"
VERSION = "5.0.0"
TASK_NAME = "Codex Usage Notifier"
SCRIPT_DIR = Path(__file__).resolve().parent
NOTIFY_SCRIPT = SCRIPT_DIR / "notify.ps1"
UI_SCRIPT = SCRIPT_DIR / "live-widget.ps1"


def _default_data_dir() -> Path:
    override = os.environ.get("CODEX_USAGE_NOTIFIER_DATA_DIR")
    if override:
        return Path(os.path.expandvars(os.path.expanduser(override))).resolve()
    if os.name == "nt":
        local = os.environ.get("LOCALAPPDATA")
        if local:
            return Path(local) / APP_SLUG
    return Path.home() / f".{APP_SLUG.lower()}"


DATA_DIR = _default_data_dir()
CONFIG_FILE = DATA_DIR / "config.json"
STATE_FILE = DATA_DIR / "state.json"
STATE_BACKUP_FILE = DATA_DIR / "state.backup.json"
HEARTBEAT_FILE = DATA_DIR / "heartbeat.json"
UI_HEARTBEAT_FILE = DATA_DIR / "ui-heartbeat.json"
LOG_FILE = DATA_DIR / "monitor.log"
APP_SERVER_LOG_FILE = DATA_DIR / "app-server.log"
LOCK_FILE = DATA_DIR / "monitor.lock"

DEFAULT_CONFIG: dict[str, Any] = {
    "schema_version": 5,
    "codex_command": "codex",
    "poll_seconds": 60,
    "post_reset_poll_seconds": 10,
    "freshness_probe_delay_seconds": 2,
    "confirmation_reads": 2,
    "maximum_confirmation_reads": 3,
    "confirmation_delay_seconds": 4,
    "request_timeout_seconds": 30,
    "refresh_auth_every_hours": 6,
    "minimum_increase_percent": 0.01,
    "notify_above_percent": 1.0,
    "notify_on_any_increase": True,
    "notify_on_limit_cleared": True,
    "notify_on_new_reset_credit": True,
    "monitor_limit_ids": [],
    "usage_url": "https://chatgpt.com/codex/settings/usage",
    "ui": {
        "live_widget": True,
        "always_on_top": True,
        "preferred_meter": "auto",
        "stale_after_seconds": 180,
        "refresh_milliseconds": 1000,
        "show_reset_countdown": True,
    },
    "notification": {
        "toast": True,
        "tray_balloon": True,
        "popup": True,
        "sound": True,
        "popup_seconds": 60,
        "ack_timeout_seconds": 30,
        "defer_when_session_locked": True,
    },
    "error_alert_after_consecutive_failures": 5,
    "error_alert_cooldown_minutes": 120,
    "maximum_backoff_seconds": 90,
    "log_max_bytes": 2_000_000,
    "log_backup_count": 5,
}

CHATGPT_ACCOUNT_TYPES = {"chatgpt", "agentIdentity", "personalAccessToken"}


class NotifierError(RuntimeError):
    pass


class CodexNotFoundError(NotifierError):
    pass


class AppServerError(NotifierError):
    pass


class AuthenticationError(NotifierError):
    pass


@dataclasses.dataclass(frozen=True)
class Meter:
    key: str
    limit_id: str
    limit_name: str | None
    slot: str
    used_percent: float
    remaining_percent: float
    window_duration_mins: int | None
    resets_at: float | None
    reached_type: str | None
    plan_type: str | None

    def as_dict(self) -> dict[str, Any]:
        return dataclasses.asdict(self)

    @classmethod
    def from_dict(cls, value: Mapping[str, Any]) -> "Meter":
        return cls(
            key=str(value.get("key", "")),
            limit_id=str(value.get("limit_id", "")),
            limit_name=_optional_string(value.get("limit_name")),
            slot=str(value.get("slot", "")),
            used_percent=float(value.get("used_percent", 0.0)),
            remaining_percent=float(value.get("remaining_percent", 0.0)),
            window_duration_mins=_optional_int(value.get("window_duration_mins")),
            resets_at=_optional_float(value.get("resets_at")),
            reached_type=_optional_string(value.get("reached_type")),
            plan_type=_optional_string(value.get("plan_type")),
        )


@dataclasses.dataclass(frozen=True)
class Snapshot:
    fetched_at: str
    account_type: str | None
    plan_type: str | None
    meters: dict[str, Meter]
    reset_credit_count: int | None
    credit_balance: float | None
    unlimited_credits: bool | None

    def as_dict(self) -> dict[str, Any]:
        return {
            "fetched_at": self.fetched_at,
            "account_type": self.account_type,
            "plan_type": self.plan_type,
            "meters": {key: meter.as_dict() for key, meter in self.meters.items()},
            "reset_credit_count": self.reset_credit_count,
            "credit_balance": self.credit_balance,
            "unlimited_credits": self.unlimited_credits,
        }

    @classmethod
    def from_dict(cls, value: Mapping[str, Any]) -> "Snapshot":
        meters: dict[str, Meter] = {}
        raw_meters = value.get("meters")
        if isinstance(raw_meters, Mapping):
            for key, raw in raw_meters.items():
                if isinstance(raw, Mapping):
                    meters[str(key)] = Meter.from_dict(raw)
        return cls(
            fetched_at=str(value.get("fetched_at", utc_now_iso())),
            account_type=_optional_string(value.get("account_type")),
            plan_type=_optional_string(value.get("plan_type")),
            meters=meters,
            reset_credit_count=_optional_int(value.get("reset_credit_count")),
            credit_balance=_optional_float(value.get("credit_balance")),
            unlimited_credits=_optional_bool(value.get("unlimited_credits")),
        )


@dataclasses.dataclass(frozen=True)
class AlertEvent:
    kind: str
    key: str
    message: str

    def as_dict(self) -> dict[str, str]:
        return dataclasses.asdict(self)


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def _optional_string(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value).strip()
    return text or None


def _optional_float(value: Any) -> float | None:
    if value is None or isinstance(value, bool):
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _optional_int(value: Any) -> int | None:
    number = _optional_float(value)
    return None if number is None else int(round(number))


def _optional_bool(value: Any) -> bool | None:
    return value if isinstance(value, bool) else None


def get_any(mapping: Mapping[str, Any], *names: str, default: Any = None) -> Any:
    for name in names:
        if name in mapping:
            return mapping[name]
    return default


def clamp_percent(value: float) -> float:
    return round(max(0.0, min(100.0, value)), 4)


def deep_merge(base: Mapping[str, Any], override: Mapping[str, Any]) -> dict[str, Any]:
    merged: dict[str, Any] = dict(base)
    for key, value in override.items():
        if isinstance(merged.get(key), Mapping) and isinstance(value, Mapping):
            merged[key] = deep_merge(merged[key], value)
        else:
            merged[key] = value
    return merged


def ensure_data_dir() -> None:
    DATA_DIR.mkdir(parents=True, exist_ok=True)


def validate_config(value: Mapping[str, Any]) -> dict[str, Any]:
    config = deep_merge(DEFAULT_CONFIG, value)
    if int(config["schema_version"]) != 5:
        raise NotifierError("Unsupported config schema_version; reinstall or regenerate config.json")
    if int(config["poll_seconds"]) < 15:
        raise NotifierError("poll_seconds must be at least 15")
    if int(config["post_reset_poll_seconds"]) < 5:
        raise NotifierError("post_reset_poll_seconds must be at least 5")
    required = int(config["confirmation_reads"])
    maximum = int(config["maximum_confirmation_reads"])
    if required < 2 or maximum < required:
        raise NotifierError("confirmation reads must require at least two reads")
    usage_url = str(config.get("usage_url", "")).strip()
    if not usage_url.startswith(("https://", "http://")):
        raise NotifierError("usage_url must be an http or https URL")
    ui = config.get("ui")
    if not isinstance(ui, Mapping):
        raise NotifierError("ui configuration must be an object")
    preferred_meter = str(ui.get("preferred_meter", "auto")).strip()
    if not preferred_meter:
        raise NotifierError("ui.preferred_meter cannot be empty")
    if int(ui.get("stale_after_seconds", 0)) < 60:
        raise NotifierError("ui.stale_after_seconds must be at least 60")
    refresh_milliseconds = int(ui.get("refresh_milliseconds", 0))
    if not 500 <= refresh_milliseconds <= 5000:
        raise NotifierError("ui.refresh_milliseconds must be between 500 and 5000")
    notification = config.get("notification")
    if not isinstance(notification, Mapping):
        raise NotifierError("notification configuration must be an object")
    if not any(bool(notification.get(k)) for k in ("toast", "tray_balloon", "popup")):
        raise NotifierError("At least one visible notification channel must be enabled")
    if int(notification.get("popup_seconds", 0)) < 5:
        raise NotifierError("notification.popup_seconds must be at least 5")
    return config


def _atomic_replace(source: Path, target: Path) -> None:
    last_error: OSError | None = None
    for attempt in range(7):
        try:
            os.replace(source, target)
            return
        except OSError as exc:
            last_error = exc
            if attempt == 6:
                break
            time.sleep(0.05 * (2**attempt))
    assert last_error is not None
    raise last_error


def atomic_write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
    temp = Path(temp_name)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(value, handle, indent=2, ensure_ascii=False, sort_keys=True)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        _atomic_replace(temp, path)
    finally:
        with contextlib.suppress(OSError):
            temp.unlink()


def initialize_config(codex_command: str | None = None) -> dict[str, Any]:
    existing: Mapping[str, Any] = {}
    if CONFIG_FILE.exists():
        try:
            parsed = json.loads(CONFIG_FILE.read_text(encoding="utf-8"))
            if isinstance(parsed, Mapping):
                existing = parsed
        except (OSError, json.JSONDecodeError):
            existing = {}
    existing_schema = _optional_int(existing.get("schema_version"))
    if existing_schema is not None and existing_schema > int(DEFAULT_CONFIG["schema_version"]):
        raise NotifierError(
            "The existing config.json was created by a newer notifier version; "
            "refusing to downgrade it."
        )
    config = deep_merge(DEFAULT_CONFIG, existing)
    # Versions 1-4 used compatible keys but an older schema marker. Upgrade the
    # marker explicitly so reinstalling over an earlier package is reliable.
    config["schema_version"] = int(DEFAULT_CONFIG["schema_version"])
    if codex_command:
        config["codex_command"] = codex_command
    config = validate_config(config)
    atomic_write_json(CONFIG_FILE, config)
    return config


def load_config() -> dict[str, Any]:
    if not CONFIG_FILE.exists():
        return initialize_config()
    try:
        value = json.loads(CONFIG_FILE.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise NotifierError(f"Cannot read {CONFIG_FILE}: {exc}") from exc
    if not isinstance(value, Mapping):
        raise NotifierError(f"{CONFIG_FILE} must contain a JSON object")
    return validate_config(value)


def setup_logging(config: Mapping[str, Any], verbose: bool = False) -> None:
    ensure_data_dir()
    root = logging.getLogger()
    root.handlers.clear()
    root.setLevel(logging.DEBUG if verbose else logging.INFO)
    formatter = logging.Formatter("%(asctime)s %(levelname)s %(message)s")
    file_handler = logging.handlers.RotatingFileHandler(
        LOG_FILE,
        maxBytes=int(config["log_max_bytes"]),
        backupCount=int(config["log_backup_count"]),
        encoding="utf-8",
    )
    file_handler.setFormatter(formatter)
    root.addHandler(file_handler)
    console = logging.StreamHandler(sys.stdout)
    console.setFormatter(formatter)
    root.addHandler(console)


def parse_iso_timestamp(value: Any) -> float | None:
    if not isinstance(value, str) or not value:
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()
    except ValueError:
        return None


def default_state() -> dict[str, Any]:
    return {
        "schema_version": 4,
        "updated_at": utc_now_iso(),
        "last_snapshot": None,
        "pending_alert": None,
        "last_delivered_alert": None,
        "consecutive_failures": 0,
        "last_error_alert_at": None,
    }


def _read_state_copy(path: Path) -> tuple[dict[str, Any], float] | None:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(value, dict):
            return None
        stamp = parse_iso_timestamp(value.get("updated_at")) or path.stat().st_mtime
        return value, stamp
    except (OSError, json.JSONDecodeError, ValueError):
        return None


def _read_heartbeat_snapshot() -> tuple[Mapping[str, Any], float] | None:
    try:
        value = json.loads(HEARTBEAT_FILE.read_text(encoding="utf-8"))
        if not isinstance(value, Mapping) or not isinstance(value.get("snapshot"), Mapping):
            return None
        stamp = parse_iso_timestamp(value.get("checked_at")) or HEARTBEAT_FILE.stat().st_mtime
        return value["snapshot"], stamp
    except (OSError, json.JSONDecodeError, ValueError):
        return None


def load_state() -> dict[str, Any]:
    candidates = [
        item
        for item in (_read_state_copy(STATE_FILE), _read_state_copy(STATE_BACKUP_FILE))
        if item
    ]
    heartbeat = _read_heartbeat_snapshot()
    if not candidates:
        state = default_state()
        if heartbeat is not None:
            state["last_snapshot"] = dict(heartbeat[0])
            logging.warning("Recovered the usage baseline from heartbeat.json")
        return state

    selected, state_stamp = max(candidates, key=lambda item: item[1])
    state = dict(deep_merge(default_state(), selected))
    if (
        heartbeat is not None
        and not isinstance(state.get("pending_alert"), Mapping)
        and heartbeat[1] > state_stamp + 1.0
    ):
        state["last_snapshot"] = dict(heartbeat[0])
        logging.warning("Recovered a newer usage baseline from heartbeat.json")
    return state


def save_state(state: MutableMapping[str, Any]) -> None:
    state["updated_at"] = utc_now_iso()
    atomic_write_json(STATE_FILE, state)
    try:
        atomic_write_json(STATE_BACKUP_FILE, state)
    except OSError as exc:
        logging.warning("Could not update redundant state copy: %s", exc)


def resolve_executable(command: str) -> str:
    expanded = os.path.expandvars(os.path.expanduser(command.strip().strip('"')))
    candidate = Path(expanded)
    if candidate.is_file():
        return str(candidate.resolve())
    found = shutil.which(expanded)
    if found:
        return str(Path(found).resolve())
    raise CodexNotFoundError(
        f"Codex CLI was not found: {command!r}. Install the current Codex CLI or update {CONFIG_FILE}."
    )


def build_codex_invocation(command: str, *arguments: str) -> list[str]:
    executable = resolve_executable(command)
    suffix = Path(executable).suffix.lower()
    args = [executable, *arguments]
    if suffix == ".py":
        return [sys.executable, executable, *arguments]
    if os.name == "nt" and suffix in {".cmd", ".bat"}:
        comspec = os.environ.get("COMSPEC", r"C:\Windows\System32\cmd.exe")
        return [comspec, "/d", "/s", "/c", subprocess.list2cmdline(args)]
    if os.name == "nt" and suffix == ".ps1":
        powershell = shutil.which("powershell.exe") or "powershell.exe"
        return [
            powershell,
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            executable,
            *arguments,
        ]
    return args


def _rotate_plain_log(path: Path, max_bytes: int = 2_000_000, backups: int = 3) -> None:
    try:
        if not path.exists() or path.stat().st_size < max_bytes:
            return
        oldest = path.with_name(f"{path.name}.{backups}")
        with contextlib.suppress(OSError):
            oldest.unlink()
        for index in range(backups - 1, 0, -1):
            src = path.with_name(f"{path.name}.{index}")
            dst = path.with_name(f"{path.name}.{index + 1}")
            if src.exists():
                with contextlib.suppress(OSError):
                    os.replace(src, dst)
        os.replace(path, path.with_name(f"{path.name}.1"))
    except OSError:
        pass


class AppServerClient:
    """Small JSON-RPC client for ``codex app-server`` over its default stdio transport."""

    def __init__(self, codex_command: str, request_timeout: int = 30) -> None:
        self.codex_command = codex_command
        self.request_timeout = request_timeout
        self.process: subprocess.Popen[str] | None = None
        self._condition = threading.Condition()
        self._responses: dict[int, dict[str, Any]] = {}
        self._notifications: queue.Queue[dict[str, Any]] = queue.Queue(maxsize=500)
        self._write_lock = threading.Lock()
        self._next_id = 1
        self._ended = threading.Event()
        self._reader_thread: threading.Thread | None = None
        self._stderr_thread: threading.Thread | None = None

    def __enter__(self) -> "AppServerClient":
        try:
            self.start()
        except Exception:
            self.close()
            raise
        return self

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        self.close()

    def start(self) -> None:
        if self.process and self.process.poll() is None:
            return
        ensure_data_dir()
        # Current Codex uses stdio as the default app-server transport. Avoid
        # the removed legacy transport switch so this works with the current CLI.
        invocation = build_codex_invocation(self.codex_command, "app-server")
        creationflags = 0
        startupinfo = None
        if os.name == "nt":
            creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
            startupinfo = subprocess.STARTUPINFO()
            startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            startupinfo.wShowWindow = 0
        env = os.environ.copy()
        env.setdefault("NO_COLOR", "1")
        env.setdefault("TERM", "dumb")
        env.setdefault("RUST_LOG", "error")
        try:
            self.process = subprocess.Popen(
                invocation,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
                env=env,
                creationflags=creationflags,
                startupinfo=startupinfo,
            )
        except OSError as exc:
            raise AppServerError(f"Could not start Codex app-server: {exc}") from exc
        self._ended.clear()
        self._reader_thread = threading.Thread(target=self._read_stdout, daemon=True)
        self._stderr_thread = threading.Thread(target=self._read_stderr, daemon=True)
        self._reader_thread.start()
        self._stderr_thread.start()
        self.request(
            "initialize",
            {
                "clientInfo": {
                    "name": "codex_usage_notifier",
                    "title": APP_NAME,
                    "version": VERSION,
                }
            },
        )
        self.notify("initialized", {})

    def is_alive(self) -> bool:
        return bool(self.process and self.process.poll() is None and not self._ended.is_set())

    def _send(self, payload: Mapping[str, Any]) -> None:
        process = self.process
        if process is None or process.stdin is None or process.poll() is not None:
            raise AppServerError("Codex app-server is not running")
        line = json.dumps(dict(payload), separators=(",", ":"), ensure_ascii=False)
        try:
            with self._write_lock:
                process.stdin.write(line + "\n")
                process.stdin.flush()
        except (OSError, BrokenPipeError) as exc:
            raise AppServerError(f"Could not write to Codex app-server: {exc}") from exc

    def notify(self, method: str, params: Mapping[str, Any] | None = None) -> None:
        self._send({"method": method, "params": dict(params or {})})

    def request(
        self,
        method: str,
        params: Mapping[str, Any] | None = None,
        timeout: float | None = None,
    ) -> Any:
        if not self.is_alive():
            raise AppServerError("Codex app-server is not running")
        with self._condition:
            request_id = self._next_id
            self._next_id += 1
        payload: dict[str, Any] = {"id": request_id, "method": method}
        if params is not None:
            payload["params"] = dict(params)
        self._send(payload)
        deadline = time.monotonic() + (timeout or self.request_timeout)
        with self._condition:
            while request_id not in self._responses:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise AppServerError(f"Timed out waiting for {method}")
                if self._ended.is_set():
                    raise AppServerError(f"Codex app-server exited while waiting for {method}")
                self._condition.wait(timeout=min(remaining, 0.5))
            response = self._responses.pop(request_id)
        if "error" in response:
            error = response.get("error")
            if isinstance(error, Mapping):
                message = _optional_string(error.get("message")) or json.dumps(error)
            else:
                message = str(error)
            raise AppServerError(f"{method} failed: {message}")
        return response.get("result")

    def get_notification(self, timeout: float = 0.0) -> dict[str, Any] | None:
        try:
            return self._notifications.get(timeout=timeout)
        except queue.Empty:
            return None

    def _read_stdout(self) -> None:
        process = self.process
        if process is None or process.stdout is None:
            return
        try:
            for raw in process.stdout:
                line = raw.strip()
                if not line:
                    continue
                try:
                    message = json.loads(line)
                except json.JSONDecodeError:
                    logging.warning("Non-JSON app-server stdout: %s", line[:500])
                    continue
                if not isinstance(message, dict):
                    continue
                message_id = message.get("id")
                if isinstance(message_id, int) and not isinstance(message_id, bool) and (
                    "result" in message or "error" in message
                ):
                    with self._condition:
                        self._responses[message_id] = message
                        self._condition.notify_all()
                    continue
                if (
                    isinstance(message_id, (int, str))
                    and not isinstance(message_id, bool)
                    and isinstance(message.get("method"), str)
                ):
                    with contextlib.suppress(AppServerError):
                        self._send(
                            {
                                "id": message_id,
                                "error": {
                                    "code": -32601,
                                    "message": f"Unsupported client callback: {message['method']}",
                                },
                            }
                        )
                    continue
                try:
                    self._notifications.put_nowait(message)
                except queue.Full:
                    with contextlib.suppress(queue.Empty):
                        self._notifications.get_nowait()
                    with contextlib.suppress(queue.Full):
                        self._notifications.put_nowait(message)
        finally:
            self._ended.set()
            with self._condition:
                self._condition.notify_all()

    def _read_stderr(self) -> None:
        process = self.process
        if process is None or process.stderr is None:
            return
        try:
            APP_SERVER_LOG_FILE.parent.mkdir(parents=True, exist_ok=True)
            handle = APP_SERVER_LOG_FILE.open("a", encoding="utf-8")
            try:
                for raw in process.stderr:
                    text = raw.rstrip()
                    if not text:
                        continue
                    try:
                        if handle.tell() >= 2_000_000:
                            handle.close()
                            _rotate_plain_log(APP_SERVER_LOG_FILE)
                            handle = APP_SERVER_LOG_FILE.open("a", encoding="utf-8")
                    except (OSError, ValueError):
                        pass
                    handle.write(f"{utc_now_iso()} {text}\n")
                    handle.flush()
            finally:
                handle.close()
        except OSError:
            return

    def close(self) -> None:
        process = self.process
        self.process = None
        if process is None:
            return
        if process.stdin:
            with contextlib.suppress(OSError):
                process.stdin.close()
        if process.poll() is None:
            with contextlib.suppress(subprocess.TimeoutExpired):
                process.wait(timeout=2)
        if process.poll() is None and os.name == "nt":
            taskkill = shutil.which("taskkill.exe") or shutil.which("taskkill")
            if taskkill:
                with contextlib.suppress(OSError, subprocess.TimeoutExpired):
                    subprocess.run(
                        [taskkill, "/PID", str(process.pid), "/T", "/F"],
                        stdin=subprocess.DEVNULL,
                        stdout=subprocess.DEVNULL,
                        stderr=subprocess.DEVNULL,
                        timeout=5,
                        check=False,
                        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                    )
        if process.poll() is None:
            with contextlib.suppress(OSError):
                process.terminate()
            with contextlib.suppress(subprocess.TimeoutExpired):
                process.wait(timeout=3)
        if process.poll() is None:
            with contextlib.suppress(OSError):
                process.kill()
        for stream in (process.stdout, process.stderr):
            if stream:
                with contextlib.suppress(OSError):
                    stream.close()
        self._ended.set()
        for thread in (self._reader_thread, self._stderr_thread):
            if thread and thread is not threading.current_thread():
                thread.join(timeout=1)


def check_account(client: AppServerClient, refresh_token: bool) -> dict[str, Any]:
    result = client.request("account/read", {"refreshToken": refresh_token})
    if not isinstance(result, Mapping):
        raise AuthenticationError("Codex returned no account information")
    account = result.get("account")
    if not isinstance(account, Mapping):
        raise AuthenticationError("Codex is not signed in. Run repair-login.ps1 or 'codex login'.")
    account_type = _optional_string(get_any(account, "type", "auth_type"))
    if account_type == "chatgptAuthTokens":
        raise AuthenticationError(
            "Externally managed ChatGPT tokens require a host refresh callback and are not supported by this utility."
        )
    if account_type not in CHATGPT_ACCOUNT_TYPES:
        raise AuthenticationError(
            f"The active Codex authentication mode does not expose ChatGPT plan limits ({account_type or 'unknown'}). Sign in with ChatGPT."
        )
    return dict(account)


def _epoch_seconds(value: Any) -> float | None:
    number = _optional_float(value)
    if number is None or number <= 0:
        return None
    return number / 1000.0 if number > 10_000_000_000 else number


def _extract_credit_fields(
    rate_result: Mapping[str, Any], buckets: Iterable[Mapping[str, Any]]
) -> tuple[float | None, bool | None]:
    candidates: list[Mapping[str, Any]] = []
    top = rate_result.get("credits")
    if isinstance(top, Mapping):
        candidates.append(top)
    for bucket in buckets:
        credits = bucket.get("credits")
        if isinstance(credits, Mapping):
            candidates.append(credits)
    for credits in candidates:
        balance = _optional_float(credits.get("balance"))
        unlimited = _optional_bool(get_any(credits, "unlimited", "is_unlimited"))
        if balance is not None or unlimited is not None:
            return balance, unlimited
    return None, None


def normalize_snapshot(
    account: Mapping[str, Any], rate_result: Mapping[str, Any], config: Mapping[str, Any]
) -> Snapshot:
    multi = get_any(rate_result, "rateLimitsByLimitId", "rate_limits_by_limit_id")
    bucket_pairs: list[tuple[str, Mapping[str, Any]]] = []
    if isinstance(multi, Mapping) and multi:
        for raw_key, raw_bucket in multi.items():
            if isinstance(raw_bucket, Mapping):
                bucket_pairs.append((str(raw_key), raw_bucket))
    else:
        single = get_any(rate_result, "rateLimits", "rate_limits")
        if isinstance(single, Mapping):
            bucket_pairs.append((str(get_any(single, "limitId", "limit_id", default="codex")), single))

    allowed_ids = {str(item) for item in config.get("monitor_limit_ids", [])}
    account_plan = _optional_string(get_any(account, "planType", "plan_type"))
    meters: dict[str, Meter] = {}
    bucket_values: list[Mapping[str, Any]] = []

    for map_key, bucket in bucket_pairs:
        limit_id = _optional_string(get_any(bucket, "limitId", "limit_id")) or map_key
        if allowed_ids and limit_id not in allowed_ids:
            continue
        bucket_values.append(bucket)
        limit_name = _optional_string(get_any(bucket, "limitName", "limit_name"))
        reached_type = _optional_string(
            get_any(bucket, "rateLimitReachedType", "rate_limit_reached_type")
        )
        plan_type = _optional_string(get_any(bucket, "planType", "plan_type")) or account_plan
        for slot in ("primary", "secondary"):
            raw_window = bucket.get(slot)
            if not isinstance(raw_window, Mapping):
                continue
            used = _optional_float(get_any(raw_window, "usedPercent", "used_percent"))
            if used is None:
                continue
            used = clamp_percent(used)
            duration = _optional_int(
                get_any(raw_window, "windowDurationMins", "window_duration_mins")
            )
            reset = _epoch_seconds(get_any(raw_window, "resetsAt", "resets_at"))
            meter_reached: str | None = None
            if reached_type:
                lower = reached_type.lower()
                if slot in lower or lower not in {"primary", "secondary"}:
                    meter_reached = reached_type
            key = f"{limit_id}:{slot}"
            meters[key] = Meter(
                key=key,
                limit_id=limit_id,
                limit_name=limit_name,
                slot=slot,
                used_percent=used,
                remaining_percent=clamp_percent(100.0 - used),
                window_duration_mins=duration,
                resets_at=reset,
                reached_type=meter_reached,
                plan_type=plan_type,
            )

    if not meters:
        raise AppServerError("account/rateLimits/read returned no usable quota windows")

    reset_credits = get_any(rate_result, "rateLimitResetCredits", "rate_limit_reset_credits")
    reset_count = None
    if isinstance(reset_credits, Mapping):
        reset_count = _optional_int(get_any(reset_credits, "availableCount", "available_count"))
    balance, unlimited = _extract_credit_fields(rate_result, bucket_values)
    plan = account_plan or next((m.plan_type for m in meters.values() if m.plan_type), None)
    return Snapshot(
        fetched_at=utc_now_iso(),
        account_type=_optional_string(get_any(account, "type", "auth_type")),
        plan_type=plan,
        meters=meters,
        reset_credit_count=reset_count,
        credit_balance=balance,
        unlimited_credits=unlimited,
    )


def fetch_snapshot(
    client: AppServerClient, config: Mapping[str, Any], refresh_token: bool = False
) -> Snapshot:
    account = check_account(client, refresh_token=refresh_token)
    result = client.request("account/rateLimits/read")
    if not isinstance(result, Mapping):
        raise AppServerError("Codex returned an invalid rate-limit response")
    return normalize_snapshot(account, result, config)


def discard_regressed_windows(previous: Snapshot | None, current: Snapshot) -> Snapshot:
    if previous is None:
        return current
    merged = dict(current.meters)
    now = time.time()
    changed = False
    for key, before in previous.meters.items():
        after = merged.get(key)
        if after is None:
            if before.resets_at is None or before.resets_at >= now - 86_400:
                merged[key] = before
                changed = True
            continue
        if (
            before.resets_at is not None
            and after.resets_at is not None
            and after.resets_at + 1 < before.resets_at
        ):
            logging.warning("Ignoring stale %s quota window because resetsAt regressed", key)
            merged[key] = before
            changed = True
    return dataclasses.replace(current, meters=merged) if changed else current


def fetch_fresh_snapshot(
    client: AppServerClient,
    config: Mapping[str, Any],
    *,
    refresh_token: bool = False,
    previous: Snapshot | None = None,
) -> Snapshot:
    first = fetch_snapshot(client, config, refresh_token=refresh_token)
    delay = float(config.get("freshness_probe_delay_seconds", 0))
    if delay <= 0:
        return discard_regressed_windows(previous, first)
    time.sleep(delay)
    second = fetch_snapshot(client, config, refresh_token=False)
    return discard_regressed_windows(previous, second)


def window_label(duration_mins: int | None, slot: str) -> str:
    if duration_mins is None:
        return slot.capitalize()
    if duration_mins == 300:
        return "5-hour limit"
    if duration_mins == 10_080:
        return "Weekly limit"
    if duration_mins == 1_440:
        return "Daily limit"
    if duration_mins == 60:
        return "Hourly limit"
    if duration_mins % 10_080 == 0:
        return f"{duration_mins // 10_080}-week limit"
    if duration_mins % 1_440 == 0:
        return f"{duration_mins // 1_440}-day limit"
    if duration_mins % 60 == 0:
        return f"{duration_mins // 60}-hour limit"
    return f"{duration_mins}-minute limit"


def meter_label(meter: Meter) -> str:
    base = meter.limit_name or ("Codex" if meter.limit_id == "codex" else meter.limit_id)
    window = window_label(meter.window_duration_mins, meter.slot)
    return window if base.lower() == "codex" else f"{base}: {window}"


def format_percent(value: float) -> str:
    rounded = round(value, 2)
    return str(int(rounded)) if rounded.is_integer() else f"{rounded:.2f}".rstrip("0").rstrip(".")


def format_local_reset(epoch: float | None) -> str:
    if epoch is None:
        return "reset time unavailable"
    try:
        value = datetime.fromtimestamp(epoch).astimezone()
    except (OSError, OverflowError, ValueError):
        return "reset time unavailable"
    return f"resets {value.strftime('%a %d %b %Y at %H:%M')}"


def _find_previous_meter(previous: Snapshot, current: Meter) -> Meter | None:
    exact = previous.meters.get(current.key)
    if exact:
        return exact
    candidates = [
        meter
        for meter in previous.meters.values()
        if meter.slot == current.slot
        and meter.window_duration_mins == current.window_duration_mins
    ]
    if current.limit_name:
        named = [meter for meter in candidates if meter.limit_name == current.limit_name]
        if len(named) == 1:
            return named[0]
    return candidates[0] if len(candidates) == 1 else None


def detect_events(previous: Snapshot, current: Snapshot, config: Mapping[str, Any]) -> list[AlertEvent]:
    events: list[AlertEvent] = []
    minimum = float(config["minimum_increase_percent"])
    threshold = float(config["notify_above_percent"])
    for key, now_meter in current.meters.items():
        before_meter = _find_previous_meter(previous, now_meter)
        if before_meter is None:
            continue
        before = before_meter.remaining_percent
        after = now_meter.remaining_percent
        increase = after - before
        increased = bool(config["notify_on_any_increase"]) and increase >= minimum
        crossed = before <= threshold < after
        cleared = (
            bool(config["notify_on_limit_cleared"])
            and bool(before_meter.reached_type)
            and not bool(now_meter.reached_type)
        )
        if not (increased or crossed or cleared):
            continue
        label = meter_label(now_meter)
        if increase >= minimum:
            message = f"{label}: {format_percent(before)}% to {format_percent(after)}% remaining"
        else:
            message = f"{label}: the reached-limit state cleared"
        if now_meter.resets_at:
            message += f" ({format_local_reset(now_meter.resets_at)})"
        events.append(AlertEvent("capacity_increase", key, message))

    if bool(config["notify_on_new_reset_credit"]):
        before_count = previous.reset_credit_count
        after_count = current.reset_credit_count
        if before_count is not None and after_count is not None and after_count > before_count:
            events.append(
                AlertEvent(
                    "reset_credit",
                    "rate-limit-reset-credits",
                    f"Rate-limit reset credits: {before_count} to {after_count} available",
                )
            )
    return events


def _event_signature(events: Sequence[AlertEvent]) -> tuple[tuple[str, str], ...]:
    return tuple(sorted((event.kind, event.key) for event in events))


def make_alert(events: Sequence[AlertEvent]) -> tuple[str, str]:
    lines = [event.message for event in events[:6]]
    if len(events) > 6:
        lines.append(f"Plus {len(events) - 6} more changes.")
    return "Codex usage is available again", "\n".join(lines)


def snapshot_from_state(value: Any) -> Snapshot | None:
    if not isinstance(value, Mapping):
        return None
    try:
        return Snapshot.from_dict(value)
    except (TypeError, ValueError):
        return None


def event_fingerprint(events: Sequence[AlertEvent], snapshot: Snapshot) -> str:
    raw = json.dumps(
        {"events": [e.as_dict() for e in events], "snapshot": snapshot.as_dict()},
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(raw).hexdigest()


def _powershell_executable() -> str:
    for name in ("powershell.exe", "powershell"):
        found = shutil.which(name)
        if found:
            return found
    root = os.environ.get("SystemRoot") or os.environ.get("WINDIR")
    if root:
        candidate = Path(root) / "System32" / "WindowsPowerShell" / "v1.0" / "powershell.exe"
        if candidate.is_file():
            return str(candidate)
    raise NotifierError("Windows PowerShell was not found")


def windows_session_is_unlocked() -> bool:
    if os.name != "nt":
        return True
    try:
        import ctypes
        from ctypes import wintypes

        user32 = ctypes.WinDLL("user32", use_last_error=True)
        user32.OpenInputDesktop.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        user32.OpenInputDesktop.restype = ctypes.c_void_p
        user32.SwitchDesktop.argtypes = [ctypes.c_void_p]
        user32.SwitchDesktop.restype = wintypes.BOOL
        user32.CloseDesktop.argtypes = [ctypes.c_void_p]
        desktop = user32.OpenInputDesktop(0, False, 0x0100)
        if not desktop:
            return False
        try:
            return bool(user32.SwitchDesktop(desktop))
        finally:
            user32.CloseDesktop(desktop)
    except Exception:
        return True


def send_desktop_alert(
    title: str,
    message: str,
    config: Mapping[str, Any],
    *,
    wait_for_ack: bool = True,
    test_mode: bool = False,
) -> bool:
    notification = config["notification"]
    if os.name != "nt":
        logging.warning("Windows desktop alert skipped on non-Windows host: %s", message)
        return False
    if notification.get("defer_when_session_locked", True) and not windows_session_is_unlocked():
        logging.warning("Windows session is locked; alert remains pending")
        return False
    if not NOTIFY_SCRIPT.is_file():
        raise NotifierError(f"Notification helper is missing: {NOTIFY_SCRIPT}")
    ensure_data_dir()
    ack = DATA_DIR / f"notify-ack-{os.getpid()}-{time.time_ns()}.txt"
    args = [
        _powershell_executable(),
        "-Sta",
        "-NoLogo",
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        str(NOTIFY_SCRIPT),
        "-Title",
        title,
        "-Message",
        message,
        "-Seconds",
        str(int(notification["popup_seconds"])),
        "-AckFile",
        str(ack),
        "-UsageUrl",
        str(config.get("usage_url", DEFAULT_CONFIG["usage_url"])),
    ]
    if test_mode:
        args.append("-TestMode")
    if not notification["toast"]:
        args.append("-NoToast")
    if not notification["tray_balloon"]:
        args.append("-NoTrayBalloon")
    if not notification["popup"]:
        args.append("-NoPopup")
    if not notification["sound"]:
        args.append("-NoSound")
    startupinfo = subprocess.STARTUPINFO()
    startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startupinfo.wShowWindow = 0
    try:
        process = subprocess.Popen(
            args,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            startupinfo=startupinfo,
            close_fds=True,
        )
    except OSError as exc:
        logging.error("Could not start notification helper: %s", exc)
        return False
    if not wait_for_ack:
        return True
    deadline = time.monotonic() + int(notification["ack_timeout_seconds"])
    while time.monotonic() < deadline:
        if ack.exists():
            try:
                result = ack.read_text(encoding="utf-8").strip().lower()
            except OSError:
                result = ""
            with contextlib.suppress(OSError):
                ack.unlink()
            return result.startswith("ok")
        if process.poll() is not None:
            break
        time.sleep(0.1)
    with contextlib.suppress(OSError):
        ack.unlink()
    logging.error("Notification helper did not acknowledge display")
    return False


def mark_alert_delivered(state: MutableMapping[str, Any], pending: Mapping[str, Any]) -> None:
    state["last_delivered_alert"] = {
        "delivered_at": utc_now_iso(),
        "created_at": pending.get("created_at"),
        "fingerprint": pending.get("fingerprint"),
        "title": pending.get("title"),
        "message": pending.get("message"),
    }


def retry_pending_alert(state: MutableMapping[str, Any], config: Mapping[str, Any]) -> bool:
    pending = state.get("pending_alert")
    if not isinstance(pending, Mapping):
        return True
    title = _optional_string(pending.get("title")) or "Codex usage changed"
    message = _optional_string(pending.get("message")) or "Codex usage is available."
    if not send_desktop_alert(title, message, config):
        return False
    snapshot = pending.get("snapshot")
    if isinstance(snapshot, Mapping):
        state["last_snapshot"] = dict(snapshot)
    mark_alert_delivered(state, pending)
    state["pending_alert"] = None
    state["consecutive_failures"] = 0
    save_state(state)
    return True


def process_snapshot(
    state: MutableMapping[str, Any],
    current: Snapshot,
    config: Mapping[str, Any],
    confirm_fetch: Callable[[], Snapshot] | None = None,
) -> tuple[Snapshot, list[AlertEvent]]:
    previous = snapshot_from_state(state.get("last_snapshot"))
    if previous is None:
        state["last_snapshot"] = current.as_dict()
        state["pending_alert"] = None
        state["consecutive_failures"] = 0
        save_state(state)
        return current, []

    current = discard_regressed_windows(previous, current)
    events = detect_events(previous, current, config)
    confirmed = current
    if events and confirm_fetch is not None:
        required = int(config["confirmation_reads"])
        maximum = int(config["maximum_confirmation_reads"])
        delay = float(config["confirmation_delay_seconds"])
        signature = _event_signature(events)
        stable_count = 1
        reads = 1
        while stable_count < required and reads < maximum:
            if delay > 0:
                time.sleep(delay)
            sample = discard_regressed_windows(previous, confirm_fetch())
            sample_events = detect_events(previous, sample, config)
            sample_signature = _event_signature(sample_events)
            reads += 1
            confirmed = sample
            events = sample_events
            if sample_signature and sample_signature == signature:
                stable_count += 1
            elif sample_signature:
                signature = sample_signature
                stable_count = 1
            else:
                stable_count = 0
                signature = ()
        if stable_count < required:
            logging.warning("Discarding unconfirmed usage increase after %s reads", reads)
            events = []

    if not events:
        state["last_snapshot"] = confirmed.as_dict()
        state["pending_alert"] = None
        state["consecutive_failures"] = 0
        save_state(state)
        return confirmed, []

    title, message = make_alert(events)
    pending = {
        "created_at": utc_now_iso(),
        "fingerprint": event_fingerprint(events, confirmed),
        "title": title,
        "message": message,
        "events": [event.as_dict() for event in events],
        "snapshot": confirmed.as_dict(),
    }
    state["pending_alert"] = pending
    save_state(state)
    if send_desktop_alert(title, message, config):
        state["last_snapshot"] = confirmed.as_dict()
        mark_alert_delivered(state, pending)
        state["pending_alert"] = None
        state["consecutive_failures"] = 0
        save_state(state)
    return confirmed, events


def record_heartbeat(
    *,
    status: str,
    snapshot: Snapshot | None = None,
    error: str | None = None,
    consecutive_failures: int = 0,
) -> None:
    payload: dict[str, Any] = {
        "version": VERSION,
        "pid": os.getpid(),
        "status": status,
        "checked_at": utc_now_iso(),
        "consecutive_failures": consecutive_failures,
    }
    if snapshot:
        payload["snapshot"] = snapshot.as_dict()
    if error:
        payload["error"] = error[:1000]
    with contextlib.suppress(OSError):
        atomic_write_json(HEARTBEAT_FILE, payload)


def error_alert_due(state: Mapping[str, Any], config: Mapping[str, Any]) -> bool:
    failures = _optional_int(state.get("consecutive_failures")) or 0
    if failures < int(config["error_alert_after_consecutive_failures"]):
        return False
    last = parse_iso_timestamp(state.get("last_error_alert_at"))
    if last is None:
        return True
    return time.time() - last >= int(config["error_alert_cooldown_minutes"]) * 60


def handle_failure(state: MutableMapping[str, Any], config: Mapping[str, Any], error: Exception) -> None:
    failures = (_optional_int(state.get("consecutive_failures")) or 0) + 1
    state["consecutive_failures"] = failures
    save_state(state)
    record_heartbeat(
        status="error",
        error=f"{type(error).__name__}: {error}",
        consecutive_failures=failures,
    )
    if error_alert_due(state, config):
        message = (
            f"The monitor failed {failures} consecutive checks.\n"
            f"{type(error).__name__}: {str(error)[:350]}\n"
            "Run Diagnostics from the installed folder."
        )
        if send_desktop_alert("Codex usage monitor needs attention", message, config):
            state["last_error_alert_at"] = utc_now_iso()
            save_state(state)


def seconds_until_next_check(snapshot: Snapshot, config: Mapping[str, Any]) -> float:
    normal = float(config["poll_seconds"])
    post_reset = float(config["post_reset_poll_seconds"])
    now = time.time()
    delay = normal
    for meter in snapshot.meters.values():
        if meter.resets_at is None:
            continue
        seconds = meter.resets_at - now
        if 0 < seconds < delay:
            delay = max(5.0, seconds + 3.0)
        elif -300 <= seconds <= 0 and meter.remaining_percent <= float(config["notify_above_percent"]):
            delay = min(delay, post_reset)
    jitter = min(2.0, delay * 0.03)
    return max(5.0, delay + random.uniform(-jitter, jitter))


class SingleInstance:
    def __init__(self) -> None:
        self._handle: Any = None
        self._kernel32: Any = None
        self._file: Any = None

    def __enter__(self) -> "SingleInstance":
        ensure_data_dir()
        if os.name == "nt":
            import ctypes
            from ctypes import wintypes

            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
            kernel32.CreateMutexW.restype = wintypes.HANDLE
            kernel32.ReleaseMutex.argtypes = [wintypes.HANDLE]
            kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
            ctypes.set_last_error(0)
            handle = kernel32.CreateMutexW(None, True, f"Local\\{APP_SLUG}-monitor")
            if not handle:
                raise NotifierError("Could not create the monitor mutex")
            if ctypes.get_last_error() == 183:
                kernel32.CloseHandle(handle)
                raise NotifierError("Another Codex Usage Notifier instance is running")
            self._handle = handle
            self._kernel32 = kernel32
        else:
            import fcntl

            self._file = LOCK_FILE.open("a+")
            try:
                fcntl.flock(self._file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError as exc:
                self._file.close()
                raise NotifierError("Another Codex Usage Notifier instance is running") from exc
        return self

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        if os.name == "nt" and self._handle and self._kernel32:
            with contextlib.suppress(Exception):
                self._kernel32.ReleaseMutex(self._handle)
            with contextlib.suppress(Exception):
                self._kernel32.CloseHandle(self._handle)
        elif self._file:
            import fcntl

            with contextlib.suppress(OSError):
                fcntl.flock(self._file.fileno(), fcntl.LOCK_UN)
            self._file.close()


def connect_client(config: Mapping[str, Any]) -> AppServerClient:
    client = AppServerClient(
        str(config["codex_command"]), int(config["request_timeout_seconds"])
    )
    try:
        client.start()
        check_account(client, refresh_token=True)
        return client
    except Exception:
        client.close()
        raise


def run_check_once(config: Mapping[str, Any], baseline_only: bool = False) -> int:
    state = load_state()
    if not baseline_only and not retry_pending_alert(state, config):
        return 2
    with connect_client(config) as client:
        previous = snapshot_from_state(state.get("last_snapshot"))
        current = fetch_fresh_snapshot(client, config, previous=previous)
        if baseline_only:
            state["last_snapshot"] = current.as_dict()
            state["pending_alert"] = None
            state["consecutive_failures"] = 0
            save_state(state)
            record_heartbeat(status="baseline", snapshot=current)
            print_snapshot(current)
            print("\nBaseline saved. No notification was sent.")
            return 0

        def confirm() -> Snapshot:
            return fetch_snapshot(client, config)

        final, events = process_snapshot(state, current, config, confirm_fetch=confirm)
        record_heartbeat(status="ok", snapshot=final)
        print_snapshot(final)
        print(f"\nDetected {len(events)} confirmed event(s)." if events else "\nNo increase detected.")
        return 0


def wait_for_poll_or_update(client: AppServerClient, delay: float) -> str:
    deadline = time.monotonic() + delay
    while True:
        if not client.is_alive():
            raise AppServerError("Codex app-server exited unexpectedly")
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return "poll"
        notification = client.get_notification(timeout=min(1.0, remaining))
        if not notification:
            continue
        method = notification.get("method")
        if method == "account/rateLimits/updated":
            return "rate_limits"
        if method == "account/updated":
            return "account"


def run_monitor(config: Mapping[str, Any]) -> int:
    with SingleInstance():
        logging.info("%s v%s monitor started", APP_NAME, VERSION)
        state = load_state()
        client: AppServerClient | None = None
        backoff = 5.0
        max_backoff = float(config["maximum_backoff_seconds"])
        last_auth_refresh = 0.0
        fresh_next = True
        try:
            while True:
                try:
                    if not retry_pending_alert(state, config):
                        record_heartbeat(status="pending-notification")
                        time.sleep(20)
                        continue
                    if client is None or not client.is_alive():
                        if client:
                            client.close()
                        client = connect_client(config)
                        last_auth_refresh = time.monotonic()
                        fresh_next = True
                    refresh = (
                        time.monotonic() - last_auth_refresh
                        >= float(config["refresh_auth_every_hours"]) * 3600
                    )
                    previous = snapshot_from_state(state.get("last_snapshot"))
                    if fresh_next:
                        current = fetch_fresh_snapshot(
                            client, config, refresh_token=refresh, previous=previous
                        )
                        fresh_next = False
                    else:
                        current = discard_regressed_windows(
                            previous, fetch_snapshot(client, config, refresh_token=refresh)
                        )
                    if refresh:
                        last_auth_refresh = time.monotonic()

                    def confirm() -> Snapshot:
                        assert client is not None
                        return fetch_snapshot(client, config)

                    final, _ = process_snapshot(state, current, config, confirm_fetch=confirm)
                    state["consecutive_failures"] = 0
                    save_state(state)
                    record_heartbeat(status="ok", snapshot=final)
                    backoff = 5.0
                    reason = wait_for_poll_or_update(client, seconds_until_next_check(final, config))
                    if reason == "rate_limits":
                        fresh_next = True
                    elif reason == "account":
                        client.close()
                        client = None
                        fresh_next = True
                except KeyboardInterrupt:
                    return 0
                except Exception as exc:
                    logging.exception("Monitor check failed")
                    handle_failure(state, config, exc)
                    if client:
                        client.close()
                        client = None
                    time.sleep(min(max_backoff, backoff) + random.uniform(0, 2))
                    backoff = min(max_backoff, backoff * 2)
        finally:
            if client:
                client.close()


def run_subprocess_capture(invocation: Sequence[str], timeout: int = 20) -> tuple[int, str]:
    try:
        completed = subprocess.run(
            list(invocation),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
            check=False,
        )
        return completed.returncode, completed.stdout.strip()
    except (OSError, subprocess.TimeoutExpired) as exc:
        return 1, str(exc)


def print_snapshot(snapshot: Snapshot) -> None:
    print(f"Account: {snapshot.account_type or 'unknown'} ({snapshot.plan_type or 'unknown plan'})")
    for meter in sorted(
        snapshot.meters.values(),
        key=lambda item: (item.limit_id, item.window_duration_mins or 0, item.slot),
    ):
        print(
            f"- {meter_label(meter)}: {format_percent(meter.remaining_percent)}% remaining, "
            f"{format_percent(meter.used_percent)}% used; {format_local_reset(meter.resets_at)}"
        )
    if snapshot.reset_credit_count is not None:
        print(f"- Rate-limit reset credits: {snapshot.reset_credit_count}")
    if snapshot.unlimited_credits:
        print("- Additional credits: unlimited")
    elif snapshot.credit_balance is not None:
        print(f"- Additional credit balance: {snapshot.credit_balance:g}")


def run_status(config: Mapping[str, Any], as_json: bool = False) -> int:
    with connect_client(config) as client:
        snapshot = fetch_fresh_snapshot(client, config, refresh_token=True)
    if as_json:
        print(json.dumps(snapshot.as_dict(), indent=2, sort_keys=True))
    else:
        print_snapshot(snapshot)
    return 0


def run_verify_account(config: Mapping[str, Any], as_json: bool = False) -> int:
    with AppServerClient(
        str(config["codex_command"]), int(config["request_timeout_seconds"])
    ) as client:
        account = check_account(client, refresh_token=True)
    safe = {
        "type": _optional_string(get_any(account, "type", "auth_type")),
        "plan_type": _optional_string(get_any(account, "planType", "plan_type")),
    }
    if as_json:
        print(json.dumps(safe, indent=2, sort_keys=True))
    else:
        print(f"Codex account verified: {safe['type']} ({safe['plan_type'] or 'unknown plan'})")
    return 0


def run_test_alert(config: Mapping[str, Any], realistic: bool = False) -> int:
    if realistic:
        title = "Codex usage is available again"
        message = "5-hour limit: 0% to 100% remaining\nWeekly limit: 0% to 25% remaining"
    else:
        title = "Codex Usage Notifier test"
        message = "Desktop toast, topmost popup, tray alert, and sound test."
    ok = send_desktop_alert(title, message, config, test_mode=not realistic)
    print("Notification acknowledged by the helper." if ok else "Notification failed.")
    return 0 if ok else 1


def run_diagnostics(config: Mapping[str, Any], as_json: bool = False) -> int:
    checks: list[dict[str, Any]] = []

    def add(name: str, ok: bool, detail: str) -> None:
        checks.append({"name": name, "ok": ok, "detail": detail})

    add("Operating system", os.name == "nt", f"{platform.system()} {platform.release()}")
    add("Python", sys.version_info >= (3, 10), f"{sys.version.split()[0]} at {sys.executable}")
    add("Config", CONFIG_FILE.is_file(), str(CONFIG_FILE))
    add("Notification helper", NOTIFY_SCRIPT.is_file(), str(NOTIFY_SCRIPT))
    add("Live widget helper", UI_SCRIPT.is_file(), str(UI_SCRIPT))
    try:
        executable = resolve_executable(str(config["codex_command"]))
        add("Codex executable", True, executable)
        code, output = run_subprocess_capture(build_codex_invocation(str(config["codex_command"]), "--version"))
        add("Codex version", code == 0, output or f"exit code {code}")
    except Exception as exc:
        add("Codex executable", False, str(exc))
    try:
        with connect_client(config) as client:
            snapshot = fetch_fresh_snapshot(client, config, refresh_token=True)
        add("App-server rate limits", bool(snapshot.meters), f"{len(snapshot.meters)} quota window(s)")
    except Exception as exc:
        add("App-server rate limits", False, f"{type(exc).__name__}: {exc}")
    try:
        ensure_data_dir()
        probe = DATA_DIR / f".write-test-{os.getpid()}"
        probe.write_text("ok", encoding="utf-8")
        probe.unlink()
        add("Data directory writable", True, str(DATA_DIR))
    except OSError as exc:
        add("Data directory writable", False, str(exc))
    if os.name == "nt":
        schtasks = shutil.which("schtasks.exe") or shutil.which("schtasks")
        for task_name in (TASK_NAME, f"{TASK_NAME} UI", f"{TASK_NAME} Watchdog"):
            if not schtasks:
                add(f"Scheduled task: {task_name}", False, "schtasks.exe not found")
                continue
            code, output = run_subprocess_capture([schtasks, "/Query", "/TN", task_name, "/FO", "LIST"])
            add(f"Scheduled task: {task_name}", code == 0, output.splitlines()[0] if output else f"exit code {code}")
        if bool(config.get("ui", {}).get("live_widget", True)):
            try:
                heartbeat = json.loads(UI_HEARTBEAT_FILE.read_text(encoding="utf-8-sig"))
                checked = parse_iso_timestamp(heartbeat.get("checked_at"))
                age = None if checked is None else max(0.0, time.time() - checked)
                status = str(heartbeat.get("status", ""))
                fresh = age is not None and age <= 60 and status in {"ok", "stale", "waiting"}
                detail = f"status={status or 'unknown'}; age={age:.1f}s" if age is not None else "timestamp unavailable"
                add("Live widget heartbeat", fresh, detail)
            except (OSError, json.JSONDecodeError, TypeError, ValueError) as exc:
                add("Live widget heartbeat", False, str(exc))
    if as_json:
        print(json.dumps({"version": VERSION, "checks": checks}, indent=2))
    else:
        print(f"{APP_NAME} v{VERSION} diagnostics")
        for check in checks:
            print(f"[{'PASS' if check['ok'] else 'FAIL'}] {check['name']}: {check['detail']}")
    return 0 if all(check["ok"] for check in checks) else 1


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    actions = parser.add_mutually_exclusive_group()
    actions.add_argument("--monitor", action="store_true")
    actions.add_argument("--once", action="store_true")
    actions.add_argument("--baseline", action="store_true")
    actions.add_argument("--status", action="store_true")
    actions.add_argument("--verify-account", action="store_true")
    actions.add_argument("--diagnose", action="store_true")
    actions.add_argument("--test-alert", action="store_true")
    actions.add_argument("--test-reset-alert", action="store_true")
    actions.add_argument("--init-config", action="store_true")
    parser.add_argument("--codex-command")
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--verbose", action="store_true")
    parser.add_argument("--version", action="version", version=VERSION)
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    if args.init_config:
        config = initialize_config(args.codex_command)
        print(f"Config written: {CONFIG_FILE}")
        print(json.dumps(config, indent=2, sort_keys=True))
        return 0
    config = load_config()
    setup_logging(config, verbose=args.verbose)
    try:
        if args.monitor:
            return run_monitor(config)
        if args.baseline:
            return run_check_once(config, baseline_only=True)
        if args.status:
            return run_status(config, as_json=args.json)
        if args.verify_account:
            return run_verify_account(config, as_json=args.json)
        if args.diagnose:
            return run_diagnostics(config, as_json=args.json)
        if args.test_alert:
            return run_test_alert(config, realistic=False)
        if args.test_reset_alert:
            return run_test_alert(config, realistic=True)
        return run_check_once(config, baseline_only=False)
    except KeyboardInterrupt:
        return 130
    except Exception as exc:
        logging.exception("Fatal error")
        print(f"ERROR: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
