# R2FIX2: behavioral and wire-boundary repair after R2FIX1

R2FIX1 production compilation passes; protocol tests now run 89/91 green. Client/server existing module tests still need API wiring migration, which CODEX WILL HANDLE during this task as testing work. To avoid shared-file conflicts, you own R2 PRODUCTION files and may add NEW targeted test files, but do NOT edit existing module test files in this task. No shell/tests/settings or review-owned edits. Read actual declarations and FEEDBACK-R2.md. Do not claim unrun tests passed.

Additional actual failures from `build/v4-migration/R2-protocol-first-test.log`: missing api and missing requestId produce wrong exception (null dereference) instead of JsonParseException. Validate required JSON members before accessing unsafe-deserialized nonnull Kotlin properties. Preserve receiver liveness on malformed requests and all 21 R1 regressions.

Independent Codex probes (R2-first-behavior.log) preserve R1 behavior: 21/21 passed. Model probes: 13/17 passed. These FOUR actual failures must be fixed:

1. ProtocolMeld rejects valid domain CLOSED_KAN fromPlayer=-1. Domain Meld explicitly allows absent source for concealed kan. Preserve this legitimate state, with context-appropriate validation; do not change engine semantics.
2. decodeRequest accepts controlEpoch string "1". Check JSON primitive NUMBER kind before exact integer conversion. Reject fractional/overflow/missing/null numbers. Apply consistent strict numeric checks to integer DTO fields instead of Gson truncation/coercion.
3. decoded.snapshot.session.scores remains mutable. Constructor wrappers on a subset do not cover Gson-deserialized SessionView/history/final result/ScoreBreakdown/ShuffleSelection/PlayerUpdate etc. Recursively decode through validating constructors and preserve immutable collections over ALL protocol DTOs and client published views, not only representative tests. Audit the complete graph.
4. decodeResponse accepts myDrawnTile `{tileId:0,suit:DRAGON,rank:3}`. ProtocolTile constructor validation currently bypassed by Gson. Use validating decoder and maintain 136 physical identities/red fives.

Other known R2 issues still requiring actual implementation:
- ProtocolViewMapper stringifies cheat values; use client-owned typed values, no protocol/Any exposure.
- Legacy StartStacking nonempty plans are UNSUPPORTED in engine; do not drop input and silently accept it. Enforce empty-only at the boundary, no arbitrary-object toString escape.
- Preserve strict unknown-field checks at each schema object; required fields and nested invalid enum/type/range must not silently default to plausible state. Existing adapters default many fields to zero/empty, which violates the contract. Optional fields/defaults may remain only where actual DTO contract allows them.
- ProtocolParamValue adapter must reject incorrect value kind, fractions/overflow for INT/LONG and arbitrary old primitive fallback. No compatibility requested.
- ProtocolParamValue.fromRaw currently converts unsupported objects to String; reject unsupported parameters explicitly rather than lose semantics. Prefer keeping domain-facing conversion logic in server; protocol/client public DTOs must not expose Any conversion helpers.

Client interface recursive type isolation currently PASSES (R2-facade-check.log). Do not add aliases/compatibility APIs. Preserve all existing valid test assertions and 21 R1 regressions. Update delivery to accurately list completed and unresolved behavior; compilation alone is not acceptance.
