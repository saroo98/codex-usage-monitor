#!/usr/bin/env python3
from __future__ import annotations

import json
import sys


def send(value):
    sys.stdout.write(json.dumps(value, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def rate_result(used_primary=95.0, used_secondary=99.0):
    return {
        "rateLimits": {
            "limitId": "codex",
            "limitName": None,
            "primary": {
                "usedPercent": used_primary,
                "windowDurationMins": 300,
                "resetsAt": 1893456000,
            },
            "secondary": {
                "usedPercent": used_secondary,
                "windowDurationMins": 10080,
                "resetsAt": 1894060800,
            },
            "rateLimitReachedType": None,
        },
        "rateLimitsByLimitId": {
            "codex": {
                "limitId": "codex",
                "limitName": None,
                "primary": {
                    "usedPercent": used_primary,
                    "windowDurationMins": 300,
                    "resetsAt": 1893456000,
                },
                "secondary": {
                    "usedPercent": used_secondary,
                    "windowDurationMins": 10080,
                    "resetsAt": 1894060800,
                },
                "rateLimitReachedType": None,
                "planType": "plus",
            }
        },
        "rateLimitResetCredits": {"availableCount": 1, "credits": []},
        "credits": {"balance": 12.5, "unlimited": False},
    }


for line in sys.stdin:
    if not line.strip():
        continue
    message = json.loads(line)
    method = message.get("method")
    request_id = message.get("id")
    if request_id is None:
        continue
    if method == "initialize":
        send({"id": request_id, "result": {"userAgent": "fake", "platformFamily": "unix"}})
    elif method == "account/read":
        send(
            {
                "id": request_id,
                "result": {
                    "account": {"type": "chatgpt", "email": "test@example.com", "planType": "plus"},
                    "requiresOpenaiAuth": True,
                },
            }
        )
    elif method == "account/rateLimits/read":
        send({"id": request_id, "result": rate_result()})
    else:
        send({"id": request_id, "error": {"code": -32601, "message": "unknown"}})
