# R2FIX5: repair two regressions from R2FIX4

Production ownership: protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt only. Codex owns all existing tests. Do not modify tests. Built-in file tools only, no shell/builds. This is a narrow correction, not another adapter rewrite.

R2FIX4 compiled but independent acceptance failed 19/52, mostly because new roundNumber >= 1 validations reject valid zero-based rounds. Evidence: build/v4-migration/R2FIX4-resumed-interim-2.log.

1. Remove the invented roundNumber >= 1 constraints in ProtocolGameEventAdapter RoundEnded (around line 706) and ProtocolSessionViewAdapter (around line 1154). Round numbers are ZERO-BASED and valid roundNumber=0 must roundtrip. Follow actual DTO constructor constraints; do not change engine or fixtures to 1. Retain strict integer parsing.
2. ApiResponse.requestId is explicitly String? = null. ApiResponseAdapter (around line 1908) must permit absent/null requestId. Supplied non-string values must reject with JsonParseException. Preserve normal strings. Apply strict optional-string validation consistently to error/roomId/resumeToken. Do not invent a request ID or change DTO nullability. ApiRequest.requestId remains required; leave it alone.

The previous review incorrectly requested non-null ApiResponse identity; this was corrected in R2FIX4 task/feedback before delivery. The newly added test `response envelope requires request identity` encodes that mistaken requirement; Codex will correct it to test the actual nullable contract. Do not modify existing tests yourself.

Preserve the successful missing-player, invalid-enum, unknown-score-field, numeric and immutable-graph fixes. Check your other new scalar constraints against actual constructors for obvious similar overreach, but avoid speculative changes. Write delivery-ANTI-R2FIX5.md with exact changes and any unresolved items; tests not run by you. Check FEEDBACK-R2FIX5.md if present before finishing.
