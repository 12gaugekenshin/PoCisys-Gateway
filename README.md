# PoCiSys Gateway for umbrelOS

PoCiSys Gateway is a separate, privacy-preserving measurement layer between an
application and a local Ollama or OpenAI-compatible runtime. This Umbrel package
targets the private Ollama service provided by **PoCiSys GPU Runtime**.

## Honest assurance boundary

The Gateway can attest to what it directly observes while forwarding traffic:
route, time, byte counts, body hashes, reported model, provider-reported token
usage, and failures. It does **not** save prompt or response text. These records
do not prove that a response was correct, that weights were unmodified, or that
inference executed on a particular GPU.

## Security defaults

- Ollama remains private on Umbrel's app network; port `11434` is not published.
- The backend URL is deployment-managed and cannot be redirected in the UI.
- The Gateway container is unprivileged and runs as UID `10001`.
- Receipts and baselines are bounded in memory and disappear on restart.
- Only preferences and the deployment-selected backend record use `/data`.

An opt-in signed evidence window is included but disabled for the first live
installation. When enabled, it retains a bounded, ECDSA-signed hash chain of
Gateway metadata under `/data`, labels it `gateway_self_attested`, and exposes
verification status without claiming independent proof of execution.

## Local verification

```text
python tools/verify_package.py
dotnet test tests/PoCiSys.Gateway.Tests/PoCiSys.Gateway.Tests.csproj -c Release
docker build -t pocisys-gateway:dev .
```

After installation, run `tools/live_validate.py` from a container attached to
Umbrel's app network. It sends one fixed, bounded request and uses the returned
`X-PoCiSys-Receipt-Id` to confirm that the exact exchange was measured without
retaining its text.

## Integration endpoint

The private Chat sidebar provides explicit **Thinking** and **Maximum output
tokens** controls. Ollama deterministic tests default to thinking disabled with
a 256-token ceiling. Normal model reasoning remains selectable when reasoning
latency is part of the test. OpenAI-compatible requests receive the selected
`max_tokens` ceiling without assuming a provider-specific reasoning parameter.

Other Umbrel apps use `http://pocisys-gateway_server_1:8719` as their Ollama or
OpenAI-compatible base URL. The dashboard is served through Umbrel on port
`8719`.
