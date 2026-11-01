# G2 UI 15 PlayMode repair delivery

Status: **UI repair delivered; not C04, P4.5, G2, or G2-G5 acceptance. V4 remains the production baseline.**

## Binding and scope

- Worktree base/publication: cde5da1649d42151abaefc2edc5f558a287d703f; parent merge db9e5d536f618a9a6991e67498b73ce34d056f4d; accepted C03 code b33f0aad21c962a6f147dbfdb8a63fd98942d4a2.
- Final commit da1cec538da89130893845931ea9cc70c3ed8416 (parent cde5da1649d42151abaefc2edc5f558a287d703f).
- Sole tracked change: unity/Mahjong.Unity/Assets/Tests/PlayMode/TablePlayModeTests.cs; source SHA-256 4310C51D48EA45D9E5EF02C8ED67E236AE77E7317845AC322D896BAB8AE5A684; Git blob 73d06b7e64efffd27db43c0b381dbbe5af740672.
- Fresh shared build: Unity 6000.3.27f1, .NET SDK 8.0.426, Release netstandard2.1; 150 input files, 19 plugin closure outputs; build 0 warnings / 0 errors, native exit 0. Provenance check passed before Unity runs. Snapshot shared-plugin-provenance.json SHA-256 A36CF4B8A9CF75656E0F01E24373BE11E26233B088B492B519D4752D576E461C; shared-build.log SHA-256 3D7A2C548EB6702A7D1CF9F6B2A9545D3CB58884738D7F08C0511DB6F137ACCA; provenance-check.log SHA-256 5DBD51D862DD4B950EC193277B42FDED8CC2550EE17BE82E1F4B024C6EB33130.
- Unity PlayMode omitted -nographics and ran on this worktree with fresh shared assemblies. Unity operations were serialized under Local\MahjongV5P45UnityValidation. Evidence/log/temp outputs are under ignored build/ui15-luna-c4edf2fb5d26490d8e32fbee0321a390.

## Consolidated defect

**ID:** UI15-01 (15 failures, one fixture root cause)
**Severity:** MAJOR
**Location:** TablePlayModeTests.cs, CreatePlayerBaselineEnvelope; cascading Step10 null dereference. Production guard TableCommandIntentFactory.RequireSubmittable is unchanged.

**Fresh reproduction:** Read-only original candidate XML: E:/majiang-review/g2-c03-unification-ds-20261101-0943/build/g2-c03-20261101-ds/unity/playmode-gfx.xml, SHA-256 36B332C646FE2A92B5E1A4E71D1A347887CB93E7F4CD6DAAF82F5F968E875E98. Historical baseline XML SHA-256 B24A0F8D2C969F403B541B724C8BE8A60B2D11BDE5BF0751B96972B3020B3CC6. Both historical runs were 147 total / 132 pass / 15 fail / 0 skip. Fresh graphical PlayMode reproduced three representatives with Unity runner exit 2, 0 pass / 3 fail / 0 skip. XML SHA-256 CD6CAE66D5B7374E6833DCDA1C97ADEA9C7D7F77964C875C5C12A90E79FB8912; log SHA-256 5890865000CFA1131260C1EEC4C2FC3EFCD1FA5DDBF0CD53C5F7B53BE851D32E. Launcher/child lifecycle evidence is unity-import-mutex.log SHA-256 80742754DBBC7F61C42F2BD6789CD7E850E2CC1700FB89B23C44EC91D180F55C. Child was not killed; mutex was reacquired and held until natural exit.

**Actual vs expected:** Normal test fixture had private seat data but omitted PrivateView.Control. Production RequireSubmittable correctly failed closed with CONTROL_STATE_UNAVAILABLE, so pointer-to-intent-to-outbox assertions saw zero submissions. Step10 then dereferenced absent Pending (NullReferenceException at TablePlayModeTests.cs:799). Expected: normal authorized snapshots identify a human owner with matching control epoch; no-owner and stale-epoch snapshots still produce zero outbox side effects.

**Repair:** CreatePlayerBaselineEnvelope supplies PlayerControlView { ControlEpoch = 1, ControlledByComputer = false, ControlVersion = 1 }, matching its decision epoch. Added isolated R6FormalProbe01_MissingOwnerControlMustRejectInputBeforeOutbox, which exercises the real presenter input route with Control=null and asserts zero unresolved outbox records. Existing R3FormalProbe05_ControlEpochChangeMustInvalidateLocalTileMarkSelection remains and passed. No production gate, handler shortcut, or business assertion was weakened.

### All 15 original failures and first messages

The table is parsed below from the original candidate XML. Raw XML remains the evidence source.

| Probe | Original failure message |
|---|---|
| ProbeReview01_SentWithoutReceipt_MustKeepCanonicalPendingAndPreventResubmit | Expected: 1<br>  But was:  0 |
| ProbeReview02_NextRoundDoubleClick_MustSubmitOnce | next-round buttons must use pending gate<br>  Expected: 1<br>  But was:  0 |
| ProbeReview08_RealPointerTileMarkConfirmation_MustSubmit | real pointer confirmation must reach client outbox<br>  Expected: 1<br>  But was:  0 |
| R2FormalProbe01_RealAppliedReceipt_MustReleasePresenterPending | Expected: 1<br>  But was:  0 |
| R2FormalProbe08_PauseResume_MustInvalidateOldDiscardSelection | Expected: True<br>  But was:  False |
| R2FormalProbe10_AvailableCheatActivation_MustHaveReachableButtons | Expected: No Exception to be thrown<br>  But was:  <Mahjong.Unity.Table.TableIntentUnavailableException: The confirmed snapshot has no authoritative owner control state; actions fail closed.<br>  at Mahjong.Unity.Table.TableCommandIntentFactory.RequireSubmittable (Mahjong.Unity.Table.PlayerTableViewState view, Mahjong.Unity.Table.TableCommandOwnerContext owner, System.Boolean requireHumanControl) [0x000cc] in E:\majiang-review\g2-c03-unification-ds-20261101-0943\unity\Mahjong.Unity\Assets\Scripts\Table\TableCommandIntentFactory.cs:822 <br>  at Mahjong.Unity.Table.TableCommandIntentFactory.CreateSubmitSleeveBloom (Mahjong.Unity.Table.PlayerTableViewState view, Mahjong.Unity.Table.TableCommandOwnerContext owner) [0x00000] in E:\majiang-review\g2-c03-unification-ds-20261101-0943\unity\Mahjong.Unity\Assets\Scripts\Table\TableCommandIntentFactory.cs:279 <br>  at Mahjong.Unity.Tests.PlayMode.TablePlayModeTests+<>c__DisplayClass50_0.<R2FormalProbe10_AvailableCheatActivation_MustHaveReachableButtons>b__0 () [0x00000] in E:\majiang-review\g2-c03-unification-ds-20261101-0943\unity\Mahjong.Unity\Assets\Tests\PlayMode\TablePlayModeTests.cs:1002 <br>  at NUnit.Framework.Constraints.ThrowsConstraint+VoidInvocationDescriptor.Invoke () [0x00000] in <f577cd9686154d6ab10efd7e65e64366>:0 <br>  at NUnit.Framework.Constraints.ThrowsConstraint+ExceptionInterceptor.Intercept (System.Object invocation) [0x00041] in <f577cd9686154d6ab10efd7e65e64366>:0 > |
| R3FormalProbe07_OffTurnWindowMustUseOffTurnActivationCommand | Expected: No Exception to be thrown<br>  But was:  <Mahjong.Unity.Table.TableIntentUnavailableException: The confirmed snapshot has no authoritative owner control state; actions fail closed.<br>  at Mahjong.Unity.Table.TableCommandIntentFactory.RequireSubmittable (Mahjong.Unity.Table.PlayerTableViewState view, Mahjong.Unity.Table.TableCommandOwnerContext owner, System.Boolean requireHumanControl) [0x000cc] in E:\majiang-review\g2-c03-unification-ds-20261101-0943\unity\Mahjong.Unity\Assets\Scripts\Table\TableCommandIntentFactory.cs:822 <br>  at Mahjong.Unity.Table.TableCommandIntentFactory.CreateSubmitSleeveBloomOffTurn (Mahjong.Unity.Table.PlayerTableViewState view, Mahjong.Unity.Table.TableCommandOwnerContext owner) [0x00000] in E:\majiang-review\g2-c03-unification-ds-20261101-0943\unity\Mahjong.Unity\Assets\Scripts\Table\TableCommandIntentFactory.cs:387 <br>  at Mahjong.Unity.Tests.PlayMode.TablePlayModeTests+<>c__DisplayClass57_0.<R3FormalProbe07_OffTurnWindowMustUseOffTurnActivationCommand>b__0 () [0x00000] in E:\majiang-review\g2-c03-unification-ds-20261101-0943\unity\Mahjong.Unity\Assets\Tests\PlayMode\TablePlayModeTests.cs:1105 <br>  at NUnit.Framework.Constraints.ThrowsConstraint+VoidInvocationDescriptor.Invoke () [0x00000] in <f577cd9686154d6ab10efd7e65e64366>:0 <br>  at NUnit.Framework.Constraints.ThrowsConstraint+ExceptionInterceptor.Intercept (System.Object invocation) [0x00041] in <f577cd9686154d6ab10efd7e65e64366>:0 > |
| R4FormalProbe04_UnchangedNoDecisionOfferMustKeepDialogOpenAndSubmittable | unchanged confirmed offer was closed by duplicate snapshot<br>  Expected: True<br>  But was:  False |
| R5FormalProbe04_UnexpiredCheatOffersMustRemainOpenAcrossPublicDiffAndCursorAdvance | unexpired sleeve dialog must remain open across non-decision public updates<br>  Expected: True<br>  But was:  False |
| Step02_RealClick_CandidateInteraction_And_OutboxSubmission | Hand tile selection active<br>  Expected: True<br>  But was:  False |
| Step03_RepeatedClicks_And_StaleDecision_Rejection | Pending intent must be active<br>  Expected: not null<br>  But was:  null |
| Step04_Cheat_TileMark_PrimaryAndSecondarySelection | TileMark canonical command must be persisted to outbox<br>  Expected: 1<br>  But was:  0 |
| Step05_Cheat_SleeveBloom_OfferChoice | Sleeve choice must be persisted to outbox<br>  Expected: 1<br>  But was:  0 |
| Step06_Cheat_BeamSwap_SlotSelection | Beam keep original choice must be persisted to outbox<br>  Expected: 1<br>  But was:  0 |
| Step10_NegativeMatrix_UnknownRedraw_GenerationClosed_PrivateAuthSwitch_TimeoutInput | System.NullReferenceException : Object reference not set to an instance of an object |

## Acceptance results

| Suite | Result | Evidence |
|---|---|---|
| Fresh shared build and provenance | PASS; native 0, 0 warnings / 0 errors; 150 inputs / 19 outputs | Logs and provenance snapshot above |
| TableModel .NET | PASS 235/235, fail 0, skip 0, native 0 | dotnet test dotnet/tests/Mahjong.Unity.TableModel.Tests/Mahjong.Unity.TableModel.Tests.csproj -c Release --results-directory <GUID> -- --report-xunit-xml --report-xunit-xml-filename tablemodel-final.xml. XML SHA-256 260A88970E7A542B324648519395B326F2F6C50F8FFF31E9AD7D3D4B23B8FDE0; log SHA-256 043B8EB2A1D3BE6512659141111FA76A2D888070BD24937C5ABF267ED62D1539 |
| Requested targeted PlayMode set | PASS 15/15, fail 0, skip 0; Unity runner 0; no -nographics | targeted15-final.xml SHA-256 9FC58B00E71F487C98EAB34A8DE19F7096A359BAE2544EE07EB5F5206C10062C; log SHA-256 FC1989ED6B48E7B1420ACB05773695249CBBB7BEAD2C900C1ACFB52A4E55E2E7 |
| Complete PlayMode | PASS 148 total / 148 pass / 0 fail / 0 skip / 0 not-run; 148 unique fullnames; all original 147 covered; new no-owner and existing wrong-epoch negatives passed; Unity runner 0 | full-playmode.xml SHA-256 FDB387A612E88394F6D28469D6E15519117C8E5213E13A40973DA75ED94CA354; log SHA-256 8DCCCCCAC4A48CB9BEF412B86C2BEE7EDA1A712CDB91437C7C619EB8C41C1C1C |

An intermediate targeted attempt had 14/15 pass and 1 fail because I first placed the no-owner contrast inside Step10, advancing its presenter-only state before receipt reconciliation. It is preserved; the contrast was moved to an isolated test. Intermediate XML SHA-256 631803A1C71093D8EE41E25899EB2995E9D24046A2CB9D31CE9863211CB17B8D; log SHA-256 06F7473CF8489C600587F173BD30C363E36876628A4A588640E619DFE1B80A9A.

Earlier .NET invocation failures were command syntax/report-output errors, not test failures, and remain preserved: -t:Rebuild parsed as a second project (native 1; tablemodel.log SHA-256 C9A099924C3D0A7BB12CD0FC026A3291D5B2549BE11CEB384996B0C4250C20F5); reporter boolean received a path (native 1; tablemodel-rerun.log SHA-256 CA99F2316449E14A86E51B8F55B91E0A70DA89A0B0B8C0B073AAE320707F33E6); reporter filename initially included a path (native 1; tablemodel-final.log SHA-256 937F4304A1B5F7BE69CA449BC8E094A283F1D8FF540E78C8D1E77F5B0C485A2D). The corrected TableModel command and counts are shown above.

## Final scope

- Commit contains only the authorized PlayMode test file. Unity-generated plugin DLL/PDB and asset diffs were restored. git diff HEAD^ HEAD --check passed; worktree is clean.
- PASS: shared build/provenance, TableModel 235, targeted 15, full PlayMode 148.
- NOT_RUN: EditMode (fixture-only PlayMode change), Android/device, Windows Player/device, formal C04/C05/G2-G5. This UI repair does not approve those phases.
- Old Compare remains BLOCKED17 / UNTRUSTED_LOCAL and LOCAL G7-02 remains BLOCKED. No push, PR change, merge, deployment, scheduler, or completion flag action.

Evidence root: E:/majiang-review/g2-ui15-luna6-20261101-1300/build/ui15-luna-c4edf2fb5d26490d8e32fbee0321a390/
## Exact reproduction and execution commands

All Unity commands below are the exact executable and argument sequence captured in the Unity `COMMAND LINE ARGUMENTS` section. The receiver ran graphical PlayMode without `-nographics`, while holding the shared OS mutex `Local\MahjongV5P45UnityValidation`; the wrapper waited for Unity's natural exit and did not kill another process. Substitute the evidence root only if preserving a separate run; these paths identify the immutable receiver artifacts for this execution.

### Representative pre-fix reproduction (receiver run; Unity runner exit 2)

```powershell
& 'D:/Unity/Hub/Editor/6000.3.27f1/Editor/Unity.exe' `
  -batchmode `
  -projectPath 'E:/majiang-review/g2-ui15-luna6-20261101-1300/unity/Mahjong.Unity' `
  -runTests -testPlatform PlayMode `
  -testFilter 'ProbeReview01_SentWithoutReceipt_MustKeepCanonicalPendingAndPreventResubmit|R2FormalProbe10_AvailableCheatActivation_MustHaveReachableButtons|Step10_NegativeMatrix_UnknownRedraw_GenerationClosed_PrivateAuthSwitch_TimeoutInput' `
  -testResults 'E:/majiang-review/g2-ui15-luna6-20261101-1300/build/ui15-luna-c4edf2fb5d26490d8e32fbee0321a390/repro-representatives.xml' `
  -logFile 'E:/majiang-review/g2-ui15-luna6-20261101-1300/build/ui15-luna-c4edf2fb5d26490d8e32fbee0321a390/repro-representatives-unity.log'
```

This was the fresh graphical pre-fix run: 3 unique cases, 0 passed / 3 failed / 0 skipped; Unity runner native exit 2. XML SHA-256 `CD6CAE66D5B7374E6833DCDA1C97ADEA9C7D7F77964C875C5C12A90E79FB8912`; Unity log SHA-256 `5890865000CFA1131260C1EEC4C2FC3EFCD1FA5DDBF0CD53C5F7B53BE851D32E`. Wrapper/child lifecycle log SHA-256 `80742754DBBC7F61C42F2BD6789CD7E850E2CC1700FB89B23C44EC91D180F55C`; it records the import/mutex wait and natural child completion. Historical original candidate/baseline XMLs remain at the paths and hashes stated above; neither was regenerated.

### Targeted 15 after repair (receiver run; Unity runner exit 0)

```powershell
& 'D:/Unity/Hub/Editor/6000.3.27f1/Editor/Unity.exe' `
  -batchmode `
  -projectPath 'E:/majiang-review/g2-ui15-luna6-20261101-1300/unity/Mahjong.Unity' `
  -runTests -testPlatform PlayMode `
  -testFilter 'ProbeReview01_SentWithoutReceipt_MustKeepCanonicalPendingAndPreventResubmit|ProbeReview02_NextRoundDoubleClick_MustSubmitOnce|ProbeReview08_RealPointerTileMarkConfirmation_MustSubmit|R2FormalProbe01_RealAppliedReceipt_MustReleasePresenterPending|R2FormalProbe08_PauseResume_MustInvalidateOldDiscardSelection|R2FormalProbe10_AvailableCheatActivation_MustHaveReachableButtons|R3FormalProbe07_OffTurnWindowMustUseOffTurnActivationCommand|R4FormalProbe04_UnchangedNoDecisionOfferMustKeepDialogOpenAndSubmittable|R5FormalProbe04_UnexpiredCheatOffersMustRemainOpenAcrossPublicDiffAndCursorAdvance|Step02_RealClick_CandidateInteraction_And_OutboxSubmission|Step03_RepeatedClicks_And_StaleDecision_Rejection|Step04_Cheat_TileMark_PrimaryAndSecondarySelection|Step05_Cheat_SleeveBloom_OfferChoice|Step06_Cheat_BeamSwap_SlotSelection|Step10_NegativeMatrix_UnknownRedraw_GenerationClosed_PrivateAuthSwitch_TimeoutInput' `
  -testResults 'E:/majiang-review/g2-ui15-luna6-20261101-1300/build/ui15-luna-c4edf2fb5d26490d8e32fbee0321a390/targeted15-final.xml' `
  -logFile 'E:/majiang-review/g2-ui15-luna6-20261101-1300/build/ui15-luna-c4edf2fb5d26490d8e32fbee0321a390/targeted15-final-unity.log'
```

Result: 15 unique requested cases, all passed, 0 failed / 0 skipped; Unity runner native exit 0. XML SHA-256 `9FC58B00E71F487C98EAB34A8DE19F7096A359BAE2544EE07EB5F5206C10062C`; log SHA-256 `FC1989ED6B48E7B1420ACB05773695249CBBB7BEAD2C900C1ACFB52A4E55E2E7`.

### Full PlayMode after repair (receiver run; Unity runner exit 0)

```powershell
& 'D:/Unity/Hub/Editor/6000.3.27f1/Editor/Unity.exe' `
  -batchmode `
  -projectPath 'E:/majiang-review/g2-ui15-luna6-20261101-1300/unity/Mahjong.Unity' `
  -runTests -testPlatform PlayMode `
  -testResults 'E:/majiang-review/g2-ui15-luna6-20261101-1300/build/ui15-luna-c4edf2fb5d26490d8e32fbee0321a390/full-playmode.xml' `
  -logFile 'E:/majiang-review/g2-ui15-luna6-20261101-1300/build/ui15-luna-c4edf2fb5d26490d8e32fbee0321a390/full-playmode-unity.log'
```

Result: 148 unique fullnames, 148 passed / 0 failed / 0 skipped / 0 not-run; all original 147 names retained and one new no-owner negative case added; Unity runner native exit 0. XML SHA-256 `FDB387A612E88394F6D28469D6E15519117C8E5213E13A40973DA75ED94CA354`; log SHA-256 `8DCCCCCAC4A48CB9BEF412B86C2BEE7EDA1A712CDB91437C7C619EB8C41C1C1C`.

### TableModel .NET command (receiver run; native exit 0)

```powershell
& 'D:/Unity/dotnet/dotnet.exe' test `
  dotnet/tests/Mahjong.Unity.TableModel.Tests/Mahjong.Unity.TableModel.Tests.csproj `
  -c Release `
  --results-directory 'E:/majiang-review/g2-ui15-luna6-20261101-1300/build/ui15-luna-c4edf2fb5d26490d8e32fbee0321a390' `
  -- --report-xunit-xml --report-xunit-xml-filename tablemodel-final.xml
```

Result 235/235 passed, 0 failed / 0 skipped; the xUnit XML copy SHA-256 is `260A88970E7A542B324648519395B326F2F6C50F8FFF31E9AD7D3D4B23B8FDE0`, log SHA-256 `043B8EB2A1D3BE6512659141111FA76A2D888070BD24937C5ABF267ED62D1539`.

## Caller review and evidence boundary

The receiver execution above is bound to source commit `da1cec538da89130893845931ea9cc70c3ed8416`, whose parent/base was `cde5da1649d42151abaefc2edc5f558a287d703f`. The tested source file was `unity/Mahjong.Unity/Assets/Tests/PlayMode/TablePlayModeTests.cs`, SHA-256 `4310C51D48EA45D9E5EF02C8ED67E236AE77E7317845AC322D896BAB8AE5A684`, Git blob `73d06b7e64efffd27db43c0b381dbbe5af740672`. Fresh shared-build provenance snapshot SHA-256 `A36CF4B8A9CF75656E0F01E24373BE11E26233B088B492B519D4752D576E461C`; shared build log SHA-256 `3D7A2C548EB6702A7D1CF9F6B2A9545D3CB58884738D7F08C0511DB6F137ACCA`; provenance verification log SHA-256 `5DBD51D862DD4B950EC193277B42FDED8CC2550EE17BE82E1F4B024C6EB33130`. Receiver source binding record is `build/ui15-luna-c4edf2fb5d26490d8e32fbee0321a390/source-binding.json` (SHA-256 `E52A75ECC0F9223C538A808CF743C6AF87819BEA16BB66B03C47B6B4BEBB1C2B`). It records the original delivery report SHA-256 `AF58BE4FF7A59276BDF4CEC6248AFCADCD5D634494126A10153000800260B8DE`; that ignored original is preserved unchanged.

Caller independent review artifacts are from `E:/majiang-review/caller-ui15-r1-review-71c2e6a9`, not receiver reruns: `build/caller-review/REVIEW.txt` SHA-256 `F11FCEB784AB85407DE4FE3200A38C5ED90A57C742FCB086D8CDD99BCA2D3B7B`; `evidence-audit.json` SHA-256 `F21AC85AF47C7862100737D4DE913404BF66F5DF0F555A4850025860CD686F8D`; `evidence-manifest.csv` SHA-256 `2DF16150F0EF6A323B6C9BF3AC8F21085CF153AB3712D42A9B4A232D1FCF271D`. Caller independently performed a fresh Release Rebuild (native exit 0) and TableModel 235/235 (native exit 0, XML SHA-256 `F536E7692DF6DF3CF1D744002AA5E08BF145E46F0F7BEDD9CDF08DF3F1AB07B0`), and audited the existing graphics evidence: 148 unique tests passed, original 147 retained, targeted 15 all passed, with hashes/source binding matched. Caller did not rerun Unity/Player/PG/Android/CI. These are audit results of receiver artifacts, not additional Unity executions.

## Review return UI15-R1-01 and closure

**ID:** UI15-R1-01. **Severity:** MAJOR. **Location:** `doc/v5-重构/V5-G2-UI15-PLAYMODE-DELIVERY-20261101.md`.

**Reproduction command:**

```powershell
git ls-tree -r --name-only da1cec538da89130893845931ea9cc70c3ed8416 -- doc/v5-重构/V5-G2-UI15-PLAYMODE-DELIVERY-20261101.md
git show --stat --oneline da1cec538da89130893845931ea9cc70c3ed8416 -- doc/v5-重构/V5-G2-UI15-PLAYMODE-DELIVERY-20261101.md
```

**Actual vs expected:** Both commands returned no tracked report; the only report was ignored at `build/ui15-luna-c4edf2fb5d26490d8e32fbee0321a390/delivery-report.md`, SHA-256 `AF58BE4FF7A59276BDF4CEC6248AFCADCD5D634494126A10153000800260B8DE`. Expected the authorized report to be tracked at the exact path above, UTF-8 with normal LF newlines, and independently reviewable. **Repair:** this document now contains the full receiver report plus exact representative, targeted 15, full PlayMode and TableModel commands; receiver/caller evidence distinctions; native exits, hashes, mutex statement, and source/build binding. No source, test, or evidence file was changed in this document-only return.

**Acceptance assertions:** the exact path exists in the final Git tree; only the previously accepted fixture source path and this authorized report path differ from `cde5da1649d42151abaefc2edc5f558a287d703f`; `git diff --check` returns 0 and the final worktree is clean; the report SHA-256 is recorded in the handoff; original 148/148 and targeted 15/15 evidence hashes and sets remain unchanged. No Unity/plugin/.NET rerun was required or performed for this documentation-only correction.

## Final document-only return binding

This report correction is based on review of the immutable execution bound to `da1cec538da89130893845931ea9cc70c3ed8416`; it does not claim that commit contained this report. The final document-return commit and this tracked report's SHA-256 are recorded in the handoff. Relative to accepted base `cde5da1649d42151abaefc2edc5f558a287d703f`, the authorized paths are exactly the fixture file listed above and this report. No push, PR change, approval, merge, deployment, scheduler or completion-flag action was performed.