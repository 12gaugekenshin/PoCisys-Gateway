#!/usr/bin/env python3
from pathlib import Path

root = Path(__file__).resolve().parents[1]
required = [
    "Dockerfile", "umbrel-app-store.yml", "pocisys-gateway/docker-compose.yml",
    "pocisys-gateway/umbrel-app.yml", "pocisys-gateway/icon.svg",
    "src/PoCiSys.Gateway/PoCiSys.Gateway.csproj", "tools/live_validate.py",
]
missing = [name for name in required if not (root / name).is_file()]
if missing:
    raise SystemExit("Missing required files: " + ", ".join(missing))

compose = (root / "pocisys-gateway/docker-compose.yml").read_text(encoding="utf-8")
manifest = (root / "pocisys-gateway/umbrel-app.yml").read_text(encoding="utf-8")
source = (root / "src/PoCiSys.Gateway/Program.cs").read_text(encoding="utf-8")
forwarder = (root / "src/PoCiSys.Gateway/GatewayForwarder.cs").read_text(encoding="utf-8")
checks = {
    "private_backend_alias": "http://pocisys-gpu-runtime_runtime_1:11434" in compose,
    "no_ollama_port_publish": "11434:" not in compose,
    "managed_connection": 'Gateway__ConnectionManaged: "true"' in compose,
    "bounded_receipts": 'Gateway__LiveReceiptLimit: "500"' in compose,
    "evidence_opt_in": 'Gateway__PersistentEvidenceEnabled: "false"' in compose,
    "non_privileged": "privileged:" not in compose,
    "read_only_root": "read_only: true" in compose,
    "no_new_privileges": "no-new-privileges:true" in compose,
    "persistent_app_data": "${APP_DATA_DIR}/data:/data" in compose,
    "dependency_declared": "pocisys-gpu-runtime" in manifest,
    "managed_redirect_rejected": "cannot be changed from the dashboard" in source,
    "receipt_header_returned": "X-PoCiSys-Receipt-Id" in forwarder,
    "evidence_contract": (root / "docs/GATEWAY-MONITOR-CORRELATION.md").is_file(),
}
failed = [name for name, ok in checks.items() if not ok]
if failed:
    raise SystemExit("Package verification failed: " + ", ".join(failed))
print(f"Package verification passed ({len(checks)} checks).")
