#!/usr/bin/env python3
from __future__ import annotations

import json
import sys

sent_callback = False

def send(value):
    print(json.dumps(value, separators=(",", ":")), flush=True)

for line in sys.stdin:
    if not line.strip():
        continue
    msg = json.loads(line)
    rid = msg.get("id")
    method = msg.get("method")
    if rid == "callback-1" and "error" in msg:
        continue
    if rid is None:
        continue
    if method == "initialize":
        send({"id": rid, "result": {}})
        if not sent_callback:
            send({"id": "callback-1", "method": "host/unsupportedCallback", "params": {}})
            sent_callback = True
    elif method == "account/read":
        send({"id": rid, "result": {"account": {"type": "chatgpt", "planType": "plus"}}})
    elif method == "account/rateLimits/read":
        send({"id": rid, "result": {"rateLimits": {"limitId": "codex", "primary": {"usedPercent": 25, "windowDurationMins": 300, "resetsAt": 1893456000}, "secondary": None, "rateLimitReachedType": None}}})
    else:
        send({"id": rid, "error": {"code": -32601, "message": "unknown"}})
