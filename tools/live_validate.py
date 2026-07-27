#!/usr/bin/env python3
"""Run one bounded local-model request and correlate it to its PoCiSys receipt."""

import argparse
import json
import time
import urllib.request


def request_json(url, *, body=None, timeout=10):
    data = None if body is None else json.dumps(body, separators=(",", ":")).encode()
    request = urllib.request.Request(url, data=data)
    if data is not None:
        request.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.headers, json.load(response)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--gateway", default="http://pocisys-gateway_server_1:8719")
    parser.add_argument("--model", default="qwen3.5:9b-64k")
    args = parser.parse_args()
    root = args.gateway.rstrip("/")
    marker = "POCISYS-BOUNDED-VALIDATION-7F3C"
    body = {
        "model": args.model,
        "stream": False,
        "think": False,
        "messages": [{"role": "user", "content": f"Reply with exactly: {marker}"}],
        "options": {"temperature": 0, "num_ctx": 2048, "num_predict": 48},
        "keep_alive": "5m",
    }
    headers, model_reply = request_json(root + "/api/chat", body=body, timeout=60)
    receipt_id = headers.get("X-PoCiSys-Receipt-Id")
    answer = ((model_reply.get("message") or {}).get("content") or "").strip()
    receipt = None
    for _ in range(20):
        _, receipts = request_json(root + "/pocisys/api/receipts?limit=20")
        receipt = next((item for item in receipts if item.get("receipt_id") == receipt_id), None)
        if receipt:
            break
        time.sleep(0.25)
    serialized = json.dumps(receipt or {}, separators=(",", ":"))
    checks = {
        "response_received": bool(answer),
        "exact_bounded_answer": answer == marker,
        "receipt_header_present": bool(receipt_id),
        "matching_receipt_found": receipt is not None,
        "completed": bool(receipt and receipt.get("completed")),
        "model_matches": bool(receipt and receipt.get("model") == args.model),
        "request_hash_present": bool(receipt and len(receipt.get("request_sha256", "")) == 64),
        "response_hash_present": bool(receipt and len(receipt.get("response_sha256", "")) == 64),
        "prompt_not_retained": marker not in serialized,
        "answer_not_retained": answer not in serialized if answer else True,
    }
    report = {
        "ok": all(checks.values()),
        "schema": "pocisys.gateway-live-validation.v1",
        "receipt_id": receipt_id,
        "checks": checks,
        "metrics": {
            "duration_ms": receipt.get("duration_ms") if receipt else None,
            "first_output_ms": receipt.get("first_output_ms") if receipt else None,
            "tokens_per_second": receipt.get("tokens_per_second") if receipt else None,
            "input_tokens": receipt.get("input_tokens") if receipt else None,
            "output_tokens": receipt.get("output_tokens") if receipt else None,
        },
    }
    print(json.dumps(report, indent=2))
    raise SystemExit(0 if report["ok"] else 1)


if __name__ == "__main__":
    main()
