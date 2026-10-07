# C3 compile and wire configuration correction

Own ONLY client/src/main/kotlin/com/majiang/network/protocol/ProtocolJson.kt. No tests/build/shell. C andC2finished beforethisrun. Root build failed: unresolved reference `Collections` at ShuffleSelectionAdapter immutable sets. Missing `import java.util.Collections`; add it (or qualify).

Also new ShuffleConfigAdapter checks interference in0..1 but canonical engine/stacking/ShuffleConfig.kt permits any FINITE interference >=0, and MatchConfig permits same; roundtrip config interference2.0 mustwork. Align protocol bounds with canonical config (plannedDraws0..18, positive selectionMillis, strength0..1 finite, interference>=0finite). No other scope. Report delivery-ANTI-SHUFFLE-C3.md briefly. Root willbuild/test.
