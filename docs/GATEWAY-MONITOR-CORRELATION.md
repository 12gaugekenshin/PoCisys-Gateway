# Gateway-to-Monitor correlation contract

This contract keeps the Gateway and passive Monitor as separate trust domains.
It does not merge their claims or imply that either component proves inference
correctness.

## Gateway claim

`gateway_self_attested` means the Gateway signed metadata it observed while
forwarding a request. The retained record may contain route, timestamps, byte
counts, body hashes, reported model, provider-reported token counts, timing,
status, and a hashed optional session label. It never contains prompt or answer
text.

## Monitor claim

`observer_attested` means the passive Monitor signed host state it independently
observed. It may cover process identity, executable and model-file hints, ports,
resource behavior, and host timing. It does not call the model or read prompts.

## Correlation record

The future bridge creates a third record only when both sides are available:

```json
{
  "schema": "pocisys.gateway-monitor-correlation.v1",
  "gateway_receipt_hash": "64 lowercase hex characters",
  "gateway_id": "pocisys-gateway:...",
  "observer_receipt_hash": "64 lowercase hex characters",
  "observer_id": "poci-observer:...",
  "window_started_at": "RFC 3339 timestamp",
  "window_ended_at": "RFC 3339 timestamp",
  "match_basis": ["host_time_overlap", "reported_model_hint"],
  "assurance": "correlated_observations",
  "limitations": [
    "does_not_prove_answer_correctness",
    "does_not_prove_exact_weights_executed",
    "does_not_prove_gpu_execution_without_independent_hardware_evidence"
  ]
}
```

The bridge must reference signed hashes rather than copy private records. A
correlation is an auditable relationship between observations, not an upgrade
of either observation into proof.

## Acceptance rules

1. Verify both source chains and identities before correlating.
2. Reject clock windows that do not overlap within the configured tolerance.
3. Never correlate on model name alone.
4. Preserve every limitation listed above in exports and user interfaces.
5. Keep bridge records bounded and hash-chained; never retain conversation text.
