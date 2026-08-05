#!/usr/bin/env python3
from __future__ import annotations

import json
import sys

count = 0

def send(value):
    print(json.dumps(value, separators=(",", ":")), flush=True)

for line in sys.stdin:
    if not line.strip():
        continue
    msg = json.loads(line)
    rid = msg.get("id")
    method = msg.get("method")
    if rid is None:
        continue
    if method == "initialize":
        send({"id": rid, "result": {}})
    elif method == "account/read":
        send({"id": rid, "result": {"account": {"type": "chatgpt", "planType": "plus"}}})
    elif method == "account/rateLimits/read":
        count += 1
        used = 100 if count == 1 else 50
        send({"id": rid, "result": {"rateLimits": {"limitId": "codex", "primary": {"usedPercent": used, "windowDurationMins": 300, "resetsAt": 1893456000}, "secondary": None, "rateLimitReachedType": None}}})
    else:
        send({"id": rid, "error": {"code": -32601, "message": "unknown"}})
