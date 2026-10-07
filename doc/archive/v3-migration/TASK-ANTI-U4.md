# ANTI-U4: final two modal input regressions

U3 completed. Final combined validation now fails exactly two original tests in core/src/test/kotlin/com/majiang/screen/MeldChoiceScreenContractTest.kt (all 154 had passed on an intermediate U3 revision, then last U3 edits introduced these). See build/v3-migration/anti-final-validation.log and core/build/test-results/test/TEST-com.majiang.screen.MeldChoiceScreenContractTest.xml.

Own ONLY core/src/main/kotlin/com/majiang/screen/GameScreen.kt plus delivery-ANTI-U4.md. No tests or other production/build changes. Codex runs tests.

1. `each of three chi combinations can be independently previewed then committed`: unselected confirm should be consumed and leave choice modal open. Current lines3158-3159 unconditionally cancel even when confirmationAction()==null, so subsequent card click returns false. Cancel only on actual selected submission (accepted/rejected) or genuinely stale state; no auto-pick, no pass.
2. `rejected and silently stale confirmations exit without committing another candidate`: at touchDown beginning syncLegacyEndpointIfPresent updates the selection model and cancels stale modal BEFORE routing its pending confirm click, thus fallthrough returns false / could trigger unrelated actions. Capture pre-refresh modal context; when refreshing invalidates an open modal, swallow that input and leave closed, without submitting any action. Preserve special Back behavior and ensure no click-through. Existing expected attempts are 0 stale and 1 explicit rejection; both consume click and close modal.

Do not weaken compatibility validation or V3 candidate checks. Keep fix minimal. Report concise root cause and changed lines; do not claim executed tests. Finish after production edits/report so final testing sees stable files.
