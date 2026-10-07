# C2 review corrections

Own client/src/main/** and server/src/main/** only, no tests/build/shell. Read C delivery and SHUFFLE-CONTRACT. Report delivery-ANTI-SHUFFLE-C2.md.

GameClient.submitShufflePlan currently returns underlying future.whenComplete directly. close() merely resets flags, so an unresponsive transport leaves the user-facing shuffle confirmation future incomplete forever; late success still completes that future as RECORDED after close. Implement tracked client-owned output future(s), complete as CLIENT_CLOSED or exceptional on close, ignore late response and guard pending state against races. Root test client/.../ShuffleProtocolAcceptanceTest.kt demonstrates this; READ only. Check paused snap / expired authority prep serverNow >= deadline / no controller authority before submit, keeping engine final authority.

Audit new ProtocolJson fields for strict types and bounds: Gson .asInt/.asLong can truncate fractional and overflow numbers and .asString accepts numeric enums. Reject malformed integers, nonboolean booleans, nonfinite/overflow values; reject any unexpected fields on ShufflePlanRequest/ShuffleSelection that could claim seat, raw wall, seed, cost. Parse own output and old absent config safely. No need change global protocol version. Do not log private payloads.
