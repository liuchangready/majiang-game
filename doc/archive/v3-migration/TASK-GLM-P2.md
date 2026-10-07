# GLM-P2: actual Kotlin compiler feedback

Own client/src/main/kotlin/com/majiang/network/protocol/** only. GLM-P complete before start. No tests, network transport, server, engine, UI, build edits.

Actual compiler `build/v3-migration/glm-client-review.log`: ProtocolJson.kt around 200 `T.entries` cannot compile for reified type parameter T : Enum<T>. Kotlin compiler: Type parameter 'T' cannot have or inherit a companion object, cannot be left hand side of dot; unresolved entries/it. Use actual generic enum iteration API, e.g. enumValues<T>(), preserving strict reject of unknown strings.

Review for any analogous compile defects. Do not rewrite codecs again. Codex runs tests. Report delivery-GLM-P2.md.
