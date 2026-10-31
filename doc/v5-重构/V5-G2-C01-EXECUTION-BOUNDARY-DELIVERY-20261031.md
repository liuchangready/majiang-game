# V5 G2-C01 鐢熶骇鎵ц杈圭晫涓庡唴閮ㄨ€楀缁堝眬浜や粯锛?026-10-31锛?
鏇存柊锛?026-10-31锛孉sia/Shanghai銆傛湰鏂囦欢鏄?**浜や粯涓庤瘉鎹姤鍛?*锛屼笉鏄獙鏀舵壒鍑嗭紝涔熶笉鏄?PR 鍚堝苟璁板綍銆?
## 0. 缁撹鍏堣

| 椤圭洰 | 缁撴灉 | 璇存槑 |
|---|---|---|
| A锛氬叡鐢ㄧ敓浜ф墽琛岃竟鐣岋紙鍘?D-1-05(a)锛?| **PASS锛堢粍浠?PlayMode 绾э級** | 姝ｅ父 `RunMatchCore` 涓?PlayMode 浣跨敤鍚屼竴涓?`FullMatchExecutionBoundary`锛涚湡瀹炲け璐ユ潯浠躲€佸崟娆℃彁浜ゃ€佺湡瀹?observer/ledger銆乴ate-receipt 闅旂鍧囨湁瀹氬悜璐熶緥銆?|
| B1锛歚Applied receipt + 褰撳墠 RoundEnded` 缁堝眬纭 | **PASS锛堣竟鐣?璐熶緥绾э級** | 绮剧‘ Applied + 鍚岃姹?commandVersion + 鍚堟硶褰撳墠 RoundEnded 鎴愬姛锛涙棤 receipt銆丷ejected銆侀敊 request/version/round/seat/role/binding/scope 鍧囦笉璇‘璁ゃ€佷笉鎺ㄨ繘銆?|
| B2锛氬唴閮?timeout/AI 鑰楀 Pass | **PASS锛圱ableRuntime 绾э級** | 鐪熷疄 `TableActor.TickAsync`/`CompleteAiAsync` 鈫?`ApplyInternalCommandAsync`锛沇allExhausted銆佺増鏈€佷簨浠躲€?36 寮犮€佹棤閲嶅鍓綔鐢紱鏈夊鏃?reservation 瀵圭収锛涚湡瀹?reservation 浜や粯娌?`SubmitCheatAsync` 閾俱€?|
| Unity PlayMode | **PASS 7/7** | 4 涓墽琛岃竟鐣岃礋渚?+ 2 涓湡瀹?match session 鐢ㄤ緥 + 1 涓?bootstrap 鐢ㄤ緥銆?|
| fresh Windows Player 鍩烘湰鍐掔儫 | **PASS** | 鍚屼竴涓?fresh Player锛宯ative 0銆乼ool 0銆?4-entry 璇佹嵁 manifest 鏍￠獙閫氳繃銆?|
| fresh Windows Player **瀹屾暣鏁村満** | **FAIL** | 棣栨澶辫触涓庢渶缁堝け璐ュ潎淇濈暀锛涚帺瀹跺己鍒?`DrawTile` 鐨?receipt 鏈洖锛屽畬鏁存暣鍦烘湭瀹屾垚銆?*涓嶅緱鎹瀹ｇО G2 鎴?P4.5 閫氳繃銆?* |
| G2-C01 / G2 楠屾敹 | **鏈壒鍑?* | 璋冪敤鏂圭嫭绔?review 鍚庢墠鍙兘鎵瑰噯锛涙湰鎶ュ憡涓嶄綔鎵瑰噯銆俈4 浠嶄负鐢熶骇鏋舵瀯銆?|

**涓€鍙ヨ瘽**锛欰/B 涓ょ粍浠ｇ爜鍜屽畾鍚戝洖褰掑凡缁忎氦浠樺苟鍙鏍革紱fresh Player 瀹屾暣鏁村満浠嶈涓€涓己鍒舵懜鎵?receipt 缂哄彛闃绘柇銆備繚鐣欓娆″け璐ワ紝涓嶇敤 r15 鏃ф牱鏈啋鍏呮柊 Player 閫氳繃銆?
## 1. 鍩虹嚎涓庡伐浣滄爲

- 鍐荤粨缁ф壙 base锛歚c3c95af6023e13915656acd686269475d5dcceb8`
- 宸ヤ綔鏍戯細`D:/majiang-wt/g2-c01-execution-boundary-ds-20261031`
- 鍒嗘敮锛歚wt/v5-g2-c01-execution-boundary-ds-20261031`
- 浠ｇ爜/娴嬭瘯鎻愪氦锛歚5b52df3ff2746c72fc3e27f5dc8b925fcb3abbd9`
- 鏈姤鍛婂崟鐙彁浜わ紙鎶ュ憡-only HEAD锛夛紱`base..HEAD` 鐨勬渶缁堝樊寮備笌 clean 鐘舵€佸湪浜ゆ帴娑堟伅涓粰鍑恒€?- 缁ф壙鐨?r15 12 灞€鏁村満鎴愭灉鍜?D-1-05(b) 缁х画浣滀负鍘嗗彶閫氳繃璇佹嵁淇濈暀锛涙湰浠诲姟娌℃湁閲嶈窇銆佹病鏈夋敼鍐欍€佹病鏈夋妸鏃?Player 褰撲綔鏂颁唬鐮佽瘉鏄庛€?- 鏃у伐浣滄爲 `D:/majiang-wt/p45-full-match-driver-ds-20261031` 鍙锛涙湭缂栬緫銆佹湭鍐欐棫 thread 鐘舵€?閿併€?
## 2. 鑼冨洿涓庢枃浠?
鏈换鍔″彧鏀逛互涓嬫枃浠讹紙`git diff --stat 5b52df3f^..5b52df3f`锛夛細

| 鏂囦欢 | 鍙樻洿 |
|---|---|
| `unity/Mahjong.Unity/Assets/Scripts/App/FullMatchExecutionBoundary.cs` | 鏂板鍏辩敤鎵ц杈圭晫锛堣鍒?鍗曟鎻愪氦/鏈夌晫绛夊緟/纭/ledger/notice/failure stage锛夈€?|
| `unity/Mahjong.Unity/Assets/Scripts/App/FullMatchExecutionBoundary.cs.meta` | Unity 璧勬簮鍏冩暟鎹€?|
| `unity/Mahjong.Unity/Assets/Scripts/App/LocalFullMatchPlayerDriver.cs` | 姝ｅ父 `RunMatchCore` 鎺ュ叆鍏变韩杈圭晫锛涘垹闄ゆ祴璇曚笓鐢ㄥ奖瀛愬疄鐜帮紱姣忚疆 terminal 鍚庢斁寮冭竟鐣岀敓鍛藉懆鏈燂紱娴嬭瘯 fixture 鏀逛负娉ㄥ叆鐪熷疄褰㈢姸 transport/view + budget銆?|
| `unity/Mahjong.Unity/Assets/Scripts/App/LocalFullMatchPolicy.cs` | `FullMatchActionPlan` 淇濈暀 accepted binding role锛沗IsOwnActionEvent` 澧炲姞瑙掕壊涓€鑷存€т繚鎶ゃ€?|
| `unity/Mahjong.Unity/Assets/Tests/PlayMode/LocalFullMatchDriverPlayModeTests.cs` | 4 涓叡浜竟鐣岃礋渚嬶紱鐪熷疄 session 鐢ㄤ緥鎺ュ叆鍚屼竴杈圭晫锛汚pplied-without-event 璐熶緥銆?|
| `dotnet/tests/Mahjong.Unity.TableModel.Tests/LocalFullMatchExecutionBoundaryTests.cs` | 鏂板 21 涓畾鍚戠敤渚嬶紙鍚?Theory 灞曞紑锛夈€?|
| `dotnet/tests/Mahjong.Unity.TableModel.Tests/Mahjong.Unity.TableModel.Tests.csproj` | 鎶婄敓浜?`FullMatchExecutionBoundary.cs` 閾炬帴杩涚嫭绔嬫祴璇曠▼搴忛泦銆?|
| `dotnet/tests/Mahjong.TableRuntime.Tests/InternalExhaustedWallPassTests.cs` | 鏂板 4 涓唴閮ㄨ€楀/浜や粯鐢ㄤ緥銆?|

鏈敼锛欴omain銆丆ontracts/Wire銆丆lient銆丩ocalHost 瑁呴厤/鍥炴墽浠ｇ爜銆丆onfirmVisibleEventObserver銆丼mokeActionEventPolicy銆佹仮澶?exporter/verifier銆佺Щ鍔ㄥ钩鍙般€佽鎴?Compare/瑙嗚銆傚伐浣滄爲涓?Unity 鐢熸垚鐨?tracked DLL/瀛椾綋/Resources/Settings/slnx 鍧囧凡褰掓。鍒?ignored `build/` 骞舵仮澶?HEAD锛涙病鏈夋彁浜や簩杩涘埗娲剧敓浜х墿锛屼篃娌℃湁鍥炴粴鐢ㄦ埛鏂囦欢銆?
## 3. A锛氬叡鐢ㄧ敓浜ф墽琛岃竟鐣?
### 3.1 瀹炵幇

`FullMatchExecutionBoundary` 涓嶄緷璧?Unity API锛岃礋璐ｏ細

- **瑙勫垝**锛歚LocalFullMatchPolicy.TryCreateActionPlan` / `TryCreateMandatoryDrawPlan`锛涜嚜宸辩殑 DISCARD 鏃?decision 鏃舵湁鐣岀瓑寰呫€?- **鍗曟鎻愪氦**锛氭瘡涓€昏緫鍔ㄤ綔鍙皟鐢ㄤ竴娆?`SubmitAsync(plan.Command)`锛況equestId/scope/bytes 鐢辨棦鏈?policy 鐢熸垚锛屼笉鍦ㄦ澶勫彟閫犮€?- **鏈夌晫绛夊緟**锛歴ubmit 20s銆乧onfirmation 30s銆乨ecision wait 60s锛坄FullMatchExecutionBudget.Production`锛夈€俧ixture 鍙敞鍏ョ煭棰勭畻锛屼笉浼€犻敊璇爜銆?- **纭**锛歚ConfirmedVisibleEventObserver` + `LocalFullMatchPolicy.IsOwnActionEvent`锛汥rawTile 鐨?`Applied receipt + 褰撳墠 observed RoundEnded` 鍒嗘敮锛汸ass 鐨?state-only 鍒嗘敮銆?- **ledger**锛氱湡瀹?`Record` / `MarkResolved` / `MarkUnresolved`锛涙湭鐭ョ粨鏋滃悓 ID 涓嶉噸鍙戯紝涔熶笉鎹?ID 閲嶅仛锛涘け璐ュ悗 `Halt`锛屽悗缁?Poll 杩斿洖鍚屼竴 terminal outcome銆?- **璇佹嵁**锛歚PlanRecorded` / `ReceiptObserved` / `ActionConfirmed` / `ActionReplanned` notice 鐢辫竟鐣屼骇鐢燂紝driver 鍙牸寮忓寲锛屼笉鍐嶆墜鍐?action/request 琛屻€?- **澶辫触闃舵**锛歚Planning` / `Submission` / `Confirmation`锛岀敤浜庝繚鎸佹棦鏈?exit code 鍚堝悓锛坧lanning 鈫?42锛宻ubmit/confirm 鈫?43/44锛夈€?- **鐢熷懡鍛ㄦ湡**锛歞river 鍦ㄦ瘡涓潈濞?`RoundEnded` 璁板綍/鎻愪氦鍓嶈皟鐢?`AbandonExecutionBoundary()`锛涜繜鍒扮殑 receipt 涓嶈兘璺ㄨ疆姹℃煋涓嬩竴杞鍥俱€傝繖鏄湰杞粠鐪熷疄 Player 璇佹嵁涓慨鍑虹殑闆嗘垚缂洪櫡锛堣 搂6.3锛夈€?
`RunMatchCore` 涓嶅啀淇濈暀 `PlanStep`/鍐呰仈 observer/ledger/鎵嬪啓 receipt 鍒嗘敮锛涙棫 `RunFaultFixtureForTest(string mode)` 宸插垹闄ゃ€傛甯歌矾寰勫拰 PlayMode 閮借皟鐢ㄥ悓涓€涓被銆?
### 3.2 瀹氬悜鍥炲綊锛圱ableModel锛?1 涓柊鐢ㄤ緥锛?
- 鏃?decision锛歚OwnDiscardWithoutADecisionExpiresAtTheBoundedDecisionWaitWithoutSubmitting` 鈥斺€?鐪熷疄鐢熶骇鏈夌晫绛夊緟鍒版湡锛? 娆℃彁浜わ紝閲嶅 Poll 浠嶆槸鍚屼竴 `DECISION_WAIT_EXPIRED`銆?- never-completing submit锛歚NeverCompletingSubmitUsesTheBoundedSubmitBranchAndKeepsOneRequest` 鈥斺€?鐪熷疄 `SubmitAsync` task 涓嶅畬鎴愶紝budget 鍒版湡 `SUBMIT_TIMEOUT`锛宭edger unresolved锛? 娆℃彁浜ゃ€?- Sent 缂?receipt锛歚SentButUnresolvedReceiptTimesOutExactlyOnceAndNeverResendsTheSameId` 鈥斺€?`RECEIPT_UNRESOLVED`锛? 娆℃彁浜わ紝鍚?ID 涓嶉噸鍙戙€?- Applied 缂?event锛歚AppliedReceiptWithoutTheMatchingRoundEndedEventFailsAndStaysUnresolved` 鈥斺€?鐪熷疄 observer `HasAppliedReceipt` + 绌?閿?batch锛宍VISIBLE_EVENT_MISSING`锛宭edger unresolved锛? 娆℃彁浜ゃ€?- 鍚屾 throw / 鏈彂閫侊細`SubmissionThatThrowsSynchronouslyIsUnresolvedAndNotRetried`銆乣SubmissionNotHandedToTheTransportFailsClosedAsUnresolved` 鈥斺€?`SUBMIT_NOT_SENT`锛寀nresolved锛屼笉閲嶅彂銆?- Rejected锛歚RejectedReceiptIsADefinitiveNoOpAndIsNeverSubmittedAgain`锛坮eplanable锛夈€乣DefinitiveNonReplanableRejectionFailsClosedWithoutProgress`锛堥潪 replanable锛夈€乣RejectedDrawAtATerminalEmptyWallIsANoOpAndWaitsForTheSettlement`锛坱erminal/empty-wall no-op锛屼笉璇‘璁わ級銆?- 缁堝眬纭鎴愬姛锛歚MandatoryDrawConfirmsOnlyWithExactAppliedReceiptAndCurrentRoundEndedBatch` 鈥斺€?鏂█ command kind/requestId/scope/expectedStateVersion/roundId/payload byte-equal锛宻ubmit count 1锛岀湡瀹?ledger resolved锛宍ActionConfirmed` visibleEvent=true銆?- 璐熶緥鐭╅樀锛歚AppliedReceiptForADifferentRequestNeverConfirms`锛堥敊 request锛夛紱`WrongBatchIdentityNeverConfirmsTheAppliedRequest` 鐨?5 涓?Theory锛堥敊 recipient seat / round / binding / scope / command-version锛夛紱`SpectatorRoleBatchNeverConfirmsAPlayerRequest`锛坮ole锛夈€?- 缁堝眬鏃?receipt锛歚RoundEndedWithoutAnAppliedReceiptNeverConfirmsTheDraw` 鈥斺€?鍗充娇 terminal event 宸插湪褰撳墠 batch锛屾病鏈?Applied receipt 涔熷彧 `RECEIPT_UNRESOLVED`锛屼笉纭銆?- Rejected 涓嶈 terminal event 瑕嗙洊锛歚RejectedDrawReceiptIsNotOverriddenByAMatchingRoundEndedEvent` 鈥斺€?浠嶆槸 replan锛屼笉鏄?confirm銆?
鎵€鏈夌敤渚嬩娇鐢ㄧ湡瀹?`SessionViewState`/`TableVisibleEventBatch`/鐢熶骇 `ProjectPlayer`/鐪熷疄 observer/ledger锛涙病鏈変紶鍏ラ鏈熼敊璇爜锛屼篃娌℃湁鎵嬪啓 action/request 鏃ュ織銆?
### 3.3 Mutation 璇佹嵁锛堟渶缁堜唬鐮侊紝ignored `build/g2-c01-execution-boundary/mutation-final/`锛?
| Mutation | 鏀瑰姩 | 缁撴灉 |
|---|---|---|
| A | 鍒犻櫎 `IsOwnActionEvent` 鐨?`batch.Role != plan.Role` 淇濇姢 | `SpectatorRoleBatchNeverConfirmsAPlayerRequest` FAIL锛圕onfirmed vs Failed锛夛紝鍏朵綑 217 pass锛涙仮澶嶅搱甯屼竴鑷淬€?|
| B | Draw terminal 鍒嗘敮鏀圭敤 `observedEvent.CommandVersion` 鑰屼笉鏄?Applied receipt 鐨?commandVersion | `WrongBatchIdentityNeverConfirmsTheAppliedRequest("command-version")` FAIL锛圕onfirmed vs Failed锛夛紝鍏朵綑 217 pass锛涙仮澶嶅搱甯屼竴鑷淬€?|
| C | 鍒犻櫎 submit timeout 鍒嗘敮鐨?`_ledger.MarkUnresolved(plan)` | `NeverCompletingSubmitUsesTheBoundedSubmitBranchAndKeepsOneRequest` FAIL锛坲nresolved 鏂█锛夛紝鍏朵綑 217 pass锛涙仮澶嶅搱甯屼竴鑷淬€?|

杩欎笁鏉¤瘉鏄庘€滃垹闄?鐮村潖鐢熶骇淇濇姢浼氫娇鐩稿叧娴嬭瘯澶辫触鈥濓紝涓嶆槸瀛ょ珛 policy 缁裤€?
## 4. B1锛氱粓灞€纭涓庤礋渚?
`Advance` 鐨?DrawTile 缁堝眬鍒嗘敮鍙湪浠ヤ笅鍏ㄩ儴鎴愮珛鏃剁‘璁わ細

1. 璇ヨ姹傜殑 receipt `Disposition=Applied`銆乣ReceiptStatus=Applied`銆乣CommandVersion` 瀛樺湪锛?2. 褰撳墠 `VisibleEventBatch` 鏄?incremental锛屼笖 `StateVersion > plan.BaselineStateVersion`锛?3. 浜嬩欢鐨?`CommandVersion` 绛変簬璇?Applied receipt 鐨?commandVersion锛?4. `LocalFullMatchPolicy.IsOwnActionEvent` 鏍￠獙 round/role/session/room/table/scope/seat/binding/connection/owner锛?5. 浜嬩欢绫诲瀷涓?`RoundEnded`锛屼笖杩欐槸 DrawTile 鐨?terminal draw銆?
缂?receipt銆丷ejected銆侀敊 request/version/round/seat/role/binding/scope 鐨勫鐞嗚 搂3.2锛涘畠浠涔?`VISIBLE_EVENT_MISSING`锛岃涔?`RECEIPT_UNRESOLVED`锛岃涔?replan 鍚庣瓑寰咃紝涓嶄細鎺ㄨ繘銆俙ConfirmVisibleEventObserver` 涓?`SmokeActionEventPolicy` 娌℃湁琚慨鏀规潵鏀惧鏉′欢锛沗LocalFullMatchPolicy` 鍙鍔犱簡 role 涓€鑷存€т繚鎶ゃ€?
## 5. B2锛氬唴閮ㄨ€楀涓?reservation

`dotnet/tests/Mahjong.TableRuntime.Tests/InternalExhaustedWallPassTests.cs`锛? 涓級锛?
1. `InternalTimeoutPassAtAnExhaustedWallCommitsWallExhaustedOnce`锛氱湡瀹?`TableActor.TickAsync` 鈫?timeout 鈫?`ApplyInternalCommandAsync`锛泈all 0銆丳ass銆乄allExhausted銆佹棤 TileDrawn銆? 娆?commit銆?36 寮狅紱绗簩娆?Tick 绌恒€?2. `InternalAiPassAtAnExhaustedWallCommitsWallExhaustedOnce`锛氱湡瀹?`CompleteAiAsync` 鈫?AI 鍐呴儴鍏ュ彛锛涘悓涓婏紱閲嶅 AI 鍐崇瓥 `AiRejected`銆佺増鏈笉鍙樸€?3. `InternalAiPassWithLiveWallDrawsNormallyInsteadOfExhausting`锛氭湁澧欍€佹棤 reservation 瀵圭収锛涙甯?TileDrawn銆亀all-1銆佺増鏈?1銆佹湭 finished銆?36銆?4. `InternalAiPassDeliversARealReservationToTheOwnerDrawExactlyOnce`锛氭部鐪熷疄 `SubmitCheatAsync`锛圫leeveBloom off-turn 鈫?choice锛変骇鐢?reservation锛屽啀鐢?AI Pass 浜や粯锛涙柇瑷€ TileDrawn tile銆乣SleeveBloomReservationDelivered`銆乧ommandVersion 7銆佹棤 CheatPointsChanged銆乺eservation 娓呯┖銆?36銆?
鍏充簬 reservation 姝ｄ緥澶嶇敤锛氬凡閫氳繃鐨?`reservation_is_delivered_on_the_next_real_draw_without_a_second_charge`锛坄N1bCheatExecutionTests.cs:639`锛夊拰 `response_pass_delivers_a_reservation_to_the_next_owner_draw`锛坄:1865`锛夊師鏍蜂繚鐣欙紝娌℃湁鍙﹂€犲悓涔夋渚嬨€傛柊澧炵殑绗?4 涓敤渚嬪彧瑕嗙洊鏃㈡湁姝ｄ緥娌℃湁瑕嗙洊鐨?**AI 鍐呴儴鍏ュ彛**锛涙病鏈夋妸 Hand 鍚堟垚 `WithReservation` 褰撲綔 live-Wall 浜や粯姝ｄ緥銆傜 1銆? 涓€楀鐢ㄤ緥鐨?reservation 鍙綔涓衡€滈潪浜や粯 guard鈥濓紝涓庡閮?`ExhaustiveWallPassExternalEntryDoesNotRequireReservationDelivery` 鍚屽舰锛屽苟棰濆鏂█ reservation 鏈浜や粯銆?
娌℃湁鍙戠幇闇€瑕佷慨鏀?`TableActor` 鐨勫唴閮ㄧ己闄凤紱鏈疆鏈敼 TableActor銆?
## 6. Unity 涓?Player

### 6.1 Unity 楠岃瘉鐜

- Unity `6000.3.27f1 (672d562a42a7)`锛學indows 10 0.19045锛寈64銆?- 鍏ㄧ▼鎸佹湁鍛藉悕 mutex `Local\MahjongV5P45UnityValidation`锛汸layMode 涓?Player 浜掓枼锛涘彧绠＄悊鑷繁鎸?PID/start/executable/hash 纭鐨勮繘绋嬨€?- PlayMode 浣跨敤 `-batchmode`锛?*涓嶅姞 `-nographics`**锛涚户鎵?runner 鐨?`-nographics` 浼氬湪 fresh Library 涓婅Е鍙?TMP importer 鐨?`No graphic device` 鏈鐞嗛敊璇紝瑙?搂6.2锛夈€?
### 6.2 棣栨澶辫触涓庡伐鍏?鐜鎺㈤拡锛堝叏閮ㄤ繚鐣欙級

1. **缁ф壙鐨?Unity license 澶辫触**锛坅ttempt-1锛屾棫杞锛夛細native 198锛宍No valid Unity Editor license found`銆傚凡淇濈暀 `unity/attempts/attempt-1-license-blocked/`銆?2. **缁ф壙鐨?PlayMode 缂栬瘧澶辫触**锛坅ttempt-2锛夛細`FullMatchSessionTransport` 鏈壘鍒般€乣PlayerControlView` 浜屼箟銆傚凡鍦ㄦ渶缁堜唬鐮佷慨澶嶏紱attempt-2 鏃ュ織淇濈暀銆?3. **鏈疆 attempt-4**锛歚-testFilter '*LocalFullMatchDriverPlayModeTests*'` 琚?Unity 褰撴鍒欒В鏋愶紝`ArgumentException: Quantifier {x,y} following nothing`锛宯ative 3銆傛敼鐢ㄧ簿纭被鍨嬪悕鍚庣户缁紱attempt-4 淇濈暀銆?4. **鏈疆 attempt-5**锛歅layMode 7 渚嬩腑 6 杩囥€? 澶辫触锛涘敮涓€澶辫触鏄?`MatchModeRealSession_CompletesOneRoundAndOpensNext` 閬囧埌 TMP importer 鐨?`Unhandled log message: 'No graphic device is available...'`銆傚幓鎺?`-nographics` 鍚?attempt-6 7/7锛沘ttempt-5 淇濈暀銆?5. **attempt-7**锛歳unner 鐨?mutex 琚墠涓€涓缁堟杩涚▼閬楀純锛宍AbandonedMutexException`锛況unner 鏀逛负鎸?OS 璇箟鎺ョ鍚庣户缁€傝澶辫触涓嶆槸浠ｇ爜澶辫触锛宎ttempt-7 淇濈暀銆?
鏈€缁?PlayMode锛歛ttempt-10锛?/7锛宔xit 0锛沊ML SHA-256 `85E9958C5920C5402F62748796E1A6F11AF67D02917C74A8849312D19417E998`銆?
### 6.3 fresh Windows Player锛氶娆″け璐ャ€佹帰閽堛€佷慨澶嶃€佹渶缁堝け璐?
**棣栨澶辫触锛堟湰浠诲姟绗竴涓湡瀹?Player 澶辫触锛屽凡瀹屾暣淇濈暀锛?*

- 鍛戒护锛歚powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-full-match.ps1 -Mode All -RunDir build/g2-c01-execution-boundary/player-full-match-20261031 -TimeoutSeconds 5400`
- 鏋勫缓锛歎nity build native 0锛孭layer manifest 205 files锛宐uild-shared/check plugins 0銆?- 杩愯锛歚p45-20261031-230952-c2e282b0`锛孭layer native **43**锛宍RECEIPT_UNRESOLVED`锛宺equest `fm-draw-0ceebb1df5ee842fc40b4a709453bc88`锛宺ound `local-main-r1`锛宐aseline 83锛?0s 鏃?receipt锛沗pending=0`銆乣lastError=<none>`銆?9 涓?Discard/Pass 鍧囧凡纭锛涚涓€涓己鍒?`DrawTile` 澶辫触銆?- 璇佹嵁锛歚build/g2-c01-execution-boundary/player-first-failure-20261031-2310/`锛坰ummary/full-match.jsonl/player.log/exit/process/outbox/source/player-files/unity-build/build-shared/check-plugins + SHA-256 鍒楄〃锛夈€?
**绗簩娆″鐜?*锛歚p45-20261031-231317-dc9f7737`锛宯ative 43锛宐aseline 81锛涘悓涓€鐥囩姸銆傝瘉鎹繚鐣欏湪鍚岀洰褰?`player-second-failure-20261031-2314/`銆?
**鎺㈤拡 1锛坵all-0 瀹㈡埛绔?guard + host settle锛屽凡鍥為€€锛?*锛氫负浜嗗尯鍒嗏€滃鎴风涓嶅簲鍦ㄧ┖澧欐懜鎵撯€濅笌鈥渉ost 涓嶅簲鍙粰 AI 缁撶畻鈥濓紝鏇句复鏃舵敼 `FullMatchBoundary` wall-0 wait 鍜?`LocalTableDriver` 瀵规墍鏈?seat 缁撶畻銆傜粨鏋?`p45-20261031-233055-992351fa` 鍦?r2 state 83 杩炵画 3 娆?session recovery 鍚?`ROUND_STALLED`锛坣ative 49锛夈€傝鎺㈤拡璇佹槑 host driver 涓嶆槸杩欐潯 match 鐨勫疄闄呯粨绠?owner锛涚浉鍏虫敼鍔ㄥ凡瀹屾暣鍥為€€锛宍LocalTableDriver.cs` 鎭㈠ HEAD銆傝瘉鎹繚鐣欏湪 `player-full-match-20261031-b`銆?
**鎺㈤拡 2 / 鐪熷疄闆嗘垚缂洪櫡锛堝凡淇級**锛歚p45-20261031-234228-a05845f1` 鍦?r6 鍏堣瀵熷埌 `RoundEnded`锛坰tate 88锛宍DrawGame#175.0@cmd88`銆乣RoundEnded#176.1@cmd88`锛夛紝driver 璁板綍骞舵彁浜?r6銆佹墦寮€ r7锛?0s 鍚?r6 鐨?Applied draw receipt 鎵嶅埌锛屾棫杈圭晫浠嶆寔鏈?r6 鐨勫湪閫?plan锛屼簬鏄 r7 瑙嗗浘鍒ゆ垚 `VISIBLE_EVENT_MISSING`锛坣ative 44锛夈€傛牴鍥犳槸 **boundary 璺ㄨ疆鐢熷懡鍛ㄦ湡**锛屼笉鏄?receiver/纭鏉′欢銆備慨澶嶏細姣忎釜 terminal round 鍦?`RecordRoundCompletionAsync` 鍓嶈皟鐢?`AbandonExecutionBoundary()`锛岄攢姣?boundary/transport锛岃繜鍒?receipt 涓嶅啀璺ㄨ疆銆侾layMode round-transition 鐢ㄤ緥浠?7/7銆傝瘉鎹繚鐣欏湪 `player-full-match-20261031-final`銆?
**鏈€缁堝け璐ワ紙鏈€缁堜唬鐮侊紝蹇呴』淇濈暀锛?*

- 鍛戒护鍚屼笂锛宍-RunDir build/g2-c01-execution-boundary/player-full-match-20261031-final2`
- Unity build native 0锛汸layer manifest 205 files锛宮anifest SHA-256 `5F88A1716C7C5C5928B1C36ADAFA7062CC570E101552CE2FF5421902770B5AAE`
- 杩愯锛歚p45-20261031-234942-9ad79bef`锛孭layer native **43**锛宍RECEIPT_UNRESOLVED`
- 澶辫触璇锋眰锛歚fm-draw-fc158796e013ea8512b4a6ae3c8dc370`锛宺ound `local-main-r1`锛宐aseline 77锛?0s 鏃?receipt锛沗state=Ready`銆乣pending=0`銆乣lastError=<none>`锛涙病鏈変换浣?`round_end_observed`/`round_terminal`銆?- 璇ュけ璐ュ彂鐢熷湪 **鐜╁绗竴涓己鍒?DrawTile**锛屼笉鏄?r15 鐨?12 灞€璺緞锛沚oundary 鐨?lifecycle reset 娌℃湁鏈轰細瑙﹀彂锛堟病鏈?terminal锛夈€?- 璇佹嵁锛歚build/g2-c01-execution-boundary/player-final2-failure-20261031-2350/`锛坰ummary `6B76BD35...`銆乯sonl `B4B27DFF...`銆乪xit/outbox/player.log/source/player-files + SHA 鍒楄〃锛夈€?
**缁撹鍙ｅ緞**锛歠resh Player **鍩烘湰鍐掔儫 PASS**锛堝悓涓€涓?final2 Player锛孡ocalSmoke锛宯ative 0銆乼ool 0銆?4-entry manifest `FB0E110D...` 鏍￠獙閫氳繃锛夛紱fresh Player **瀹屾暣鏁村満 FAIL**銆備笉寰楁妸鍩烘湰鍐掔儫銆乺15 鏃ф牱鏈垨缁勪欢 7/7 鍐欐垚瀹屾暣鏁村満閫氳繃銆?
## 7. 瀹屾暣鍛戒护銆佽鏁颁笌鍘熺敓 XML

鏈€缁?.NET 璇佹嵁鍦?`build/g2-c01-execution-boundary/final-evidence-20261031-2355/`銆備负閬垮厤 mutation 鎭㈠鍚?timestamp 鏃╀簬 DLL 瀵艰嚧鐨勬棫 DLL 澧為噺鏋勫缓锛屼笁椤圭洰鍧囩敤 `-t:Rebuild` 寮哄埗鍏ㄩ噺閲嶅缓锛屽啀 `--no-build` 杩愯銆?
| Suite | Total | Pass | Fail | Skip | native exit | 鍘熺敓 XML SHA-256 |
|---|---:|---:|---:|---:|---:|---|
| `Mahjong.Unity.TableModel.Tests` | 218 | 218 | 0 | 0 | 0 | `592A4F3E9502A03E55E5144320006382F67012C1A2A6F41E4C650D2A614DA45A` |
| `Mahjong.TableRuntime.Tests` | 482 | 482 | 0 | 0 | 0 | `412EF1E330AC4CF03192518E295F1AB86E163514FAF57161B247E0F8278C2135` |
| `Mahjong.LocalHost.Tests` | 127 | 127 | 0 | 0 | 0 | `FCF0FFA4A2A2DEA42A2FF906858528F29C0C91ECAFFAE47AE3637B16C58B5F7F` |
| `VerifyP45FullMatchDriver.Tests.ps1` | 32 | 32 | 0 | 0 | 0 | log `D8315A50...` |
| Unity PlayMode `LocalFullMatchDriverPlayModeTests` | 7 | 7 | 0 | 0 | 0 | `85E9958C...` |
| Windows Player basic LocalSmoke | 鈥?| PASS | 0 | 0 | native 0 / tool 0 | manifest `FB0E110D...` |
| Windows Player full match | 鈥?| **FAIL** | 1 | 0 | native 43 / tool 10 | summary `6B76BD35...` |

鍏抽敭鍛戒护锛堝師鏍凤級锛?
```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.CrashWorker/Mahjong.LocalHost.CrashWorker.csproj -c Release -t:Rebuild
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Unity.TableModel.Tests/Mahjong.Unity.TableModel.Tests.csproj -c Release -t:Rebuild
D:/Unity/dotnet/dotnet.exe test  dotnet/tests/Mahjong.Unity.TableModel.Tests/Mahjong.Unity.TableModel.Tests.csproj -c Release --no-build -- --results-directory <dir> --report-xunit-xml --report-xunit-xml-filename results.xml
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release -t:Rebuild
D:/Unity/dotnet/dotnet.exe test  dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-build -- --results-directory <dir> --report-xunit-xml --report-xunit-xml-filename results.xml
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release -t:Rebuild
D:/Unity/dotnet/dotnet.exe test  dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --no-build -- --results-directory <dir> --report-xunit-xml --report-xunit-xml-filename results.xml
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/VerifyP45FullMatchDriver.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-full-match.ps1 -Mode All -RunDir build/g2-c01-execution-boundary/player-full-match-20261031-final2 -TimeoutSeconds 5400
```

`git diff --check` exit 0锛沗git status --porcelain` 鍦ㄦ渶缁堟彁浜ゅ悗搴斾负绌恒€俙source-code.patch` SHA-256 `E20C6A5DA07E81291F0E44CA435D961088F43F0E22A2FF1E03C27ABCB39A56ED`锛堜唬鐮?娴嬭瘯鍙樻洿锛屾帓闄?Unity 鐢熸垚鐗╋級銆?
## 8. 缂洪櫡鏄犲皠

| 鍘熺己闄?| 鏈疆澶勭悊 | 鐘舵€?|
|---|---|---|
| D-1-05(a) 澶辫触 fixture 鐩村啓 Fail锛岀粫杩囩敓浜ц矾寰?| 鎶藉彇 `FullMatchExecutionBoundary`锛涙甯?PlayMode 鍏辩敤锛涘垹闄ゅ奖瀛?fixture锛涚湡瀹炲け璐ユ潯浠?+ ledger + observer + 鍗曟鎻愪氦 + 鍚?ID 涓嶉噸鍙戯紱mutation 璇佹槑淇濇姢銆?| 缁勪欢/PlayMode 绾у叧闂紱瀹屾暣 Player 浠嶅彈 搂6.3 receipt 缂哄彛闃绘柇銆?|
| D-1-05(c) 缁堝眬纭 `Applied + RoundEnded` | 绾冲叆鍏变韩杈圭晫锛涚簿纭垚鍔?+ 鏃?receipt/Rejected/閿?request/version/round/seat/role/binding/scope 璐熶緥锛沴ate-receipt 璺ㄨ疆鐢熷懡鍛ㄦ湡淇銆?| 杈圭晫绾у叧闂紱鐪熷疄 Player 鐨勫己鍒?Draw receipt 浠嶆湭鍥炪€?|
| D-1-05(c) 鍐呴儴 timeout/AI 鑰楀銆佹湁澧欐棤 reservation銆佺湡瀹?reservation 浜や粯 | 4 涓湡瀹?TableActor 鍐呴儴鐢ㄤ緥锛涘閮ㄥ叆鍙ｆ渚嬩繚鐣欙紱reservation 姝ｄ緥澶嶇敤鏄犲皠銆?| 缁勪欢绾у叧闂€?|
| D-1-05(b) match-channel deadline 閫€褰规棫 decision | 鏈噸寮€锛汱ocalHost 127/127 浠嶅寘鍚?`RealMatchChannelDeadlineRetiresDecisionAndRejectsStaleControl`銆?| 淇濇寔鍏抽棴锛堝巻鍙叉垚鏋滐級銆?|
| r15 12 灞€鏁村満 | 鏈噸璺戞浛浠ｏ紱鍘嗗彶璇佹嵁淇濈暀锛涙柊 full-match 澶辫触涓嶆帹缈?r15 鍘嗗彶锛屼絾涔熶笉鑳界敤 r15 璇佹槑鏂颁唬鐮併€?| 鍘嗗彶閫氳繃锛岄潪鏈疆 fresh 璇佹槑銆?|

## 9. 鏈叧闂」 / 骞冲彴杈圭晫

1. **Fresh Player 瀹屾暣鏁村満 FAIL锛圡AJOR/BLOCKER 绾э級**锛氱帺瀹堕涓己鍒?`DrawTile` 鍋跺皵 30s 鏃?receipt锛屼笖娌℃湁 RoundEnded push銆傚綋鍓嶈瘉鎹畾浣嶅埌 client persist-before-send / LocalHost draw鈫抯ettlement receipt 浜ょ晫锛?   - `dotnet/src/Mahjong.Client/TableSession.cs:344`锛坧ersist-before-send锛宍Sent=true` 鍚庣瓑寰?`ResolveTrackedAsync`/`ReceiptResolved`锛夛紱
   - `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs:865`锛坵all-0 DrawTile 鈫?ExhaustiveDraw settlement conversion锛夛紱
   - `dotnet/src/Mahjong.LocalHost/LocalTableDriver.cs:186`锛坔uman Draw 涓嶄富鍔ㄧ粨绠楋紱AI-only 鍒嗘敮锛夛紱
   - `dotnet/src/Mahjong.Client/TableSession.cs:1716`锛坄ReceiptResolved` 鍙湪 `ResolveTrackedAsync` 鏃惰Е鍙戯級銆?   杩欎簺鏂囦欢涓嶅湪鏈换鍔?A/B 鍏佽鑼冨洿锛涙湭鏀?Client/LocalHost 鍥炴墽瀹炵幇锛屼篃鏈敤鏀惧/閲嶈瘯鎺╃洊銆傞渶瑕佺嫭绔?host-receipt 浠诲姟鎴栬皟鐢ㄦ柟鏄庣‘鎺堟潈鍚庡啀淇€?2. **Android/iOS/绉诲姩骞冲彴銆佽鎴樸€丆ompare/鎭㈠瀵煎嚭銆佽瑙夋埅鍥剧粨璁?*锛歂OT_RUN/鏈鐩栵紱鍩烘湰 Player 鍐掔儫鍙瘉鏄?Windows hidden Player 鐨勫惎鍔?鑿滃崟/澶у巺/涓€灞€/涓€鍔ㄤ綔/閫€鍑猴紝涓嶈瘉鏄庢暣鍦恒€佷笁缁濇妧銆丄I銆佹祦灞€/鍜岀墝鎴栬瑙夈€?3. **G2/P4.5 鏈壒鍑?*锛欰/B 缁勪欢缁夸笉绛変簬闃舵缁匡紱璋冪敤鏂圭嫭绔?review 鍚庢墠鍙兘鎵瑰噯锛孭R20/PR24 缁熶竴鏀跺彛涓嶅湪鏈换鍔℃墽琛屻€?4. **r15 鏃ф牱鏈?*锛氬彧浣滀负鍘嗗彶閫氳繃璇佹嵁锛涗笉浣滀负鏂颁唬鐮?fresh Player 閫氳繃璇佹槑銆?
## 10. 澶嶆牳鍏ュ彛

- 浠ｇ爜/娴嬭瘯锛歚5b52df3ff2746c72fc3e27f5dc8b925fcb3abbd9`
- 缁勪欢璇佹嵁绱㈠紩锛歚build/g2-c01-execution-boundary/final-evidence-20261031-2355/evidence-index.json`
- PlayMode锛歚build/g2-c01-execution-boundary/unity/attempts/attempt-10/`
- Player 棣栨澶辫触锛歚build/g2-c01-execution-boundary/player-first-failure-20261031-2310/`
- Player 鏈€缁堝け璐ワ細`build/g2-c01-execution-boundary/player-final2-failure-20261031-2350/`
- Player 鍩烘湰鍐掔儫锛歚build/g2-c01-execution-boundary/player-basic-smoke-20261031/`
- Mutation锛歚build/g2-c01-execution-boundary/mutation-final/`
- 鐢熸垚鐗╂仮澶嶏細`build/g2-c01-execution-boundary/generated-final-restore-20261031-2352/`

鏈姤鍛婂彧琛ㄧず鏈疆浠ｇ爜/娴嬭瘯浜や粯涓庤瘉鎹紝涓嶈〃绀?G2-C01 鎴?G2 宸查€氳繃锛屼篃涓嶆瀯鎴?PR 鍚堝苟銆侀儴缃叉垨 complete.flag銆?
---

## 11. R3 杩斾慨锛?026-11-01锛?
鏈妭鏄?R3 杩斾慨浜や粯锛岃鐩栨湰杞?3 涓?MAJOR 涓凡鎺堟潈鐨勪袱缁勶紝骞剁籂姝?R2 鎶ュ憡鐨勮秺鐣屼笌鎺ㄦ柇銆俁1 姝ｆ枃涓庡巻鍙茶瘉鎹繚鐣欍€?
### 11.1 C01-R2-01锛歵erminal 浜嬩欢鍘嗗彶锛堝凡淇锛?
R2 鐨?Draw terminal 鍒嗘敮鍙 `_transport.View.Current.VisibleEventBatch`锛沷bserver 宸茶瀵熷埌鐨?terminal 浼氳鍚庣画绌?涓嶅悓鐗堟湰 publication 瑕嗙洊銆俁3 鏀逛负閬嶅巻 `ConfirmedVisibleEventObserver.Batches` 鐨勪笉鍙彉鍘嗗彶锛屼粛鐢ㄦ棦鏈?`LocalFullMatchPolicy.IsOwnActionEvent` 鐨?round/role/scope/seat/binding/connection/owner/commandVersion 鍏ㄩ噺 fence 閰嶅锛沗View.Current` 涓嶅啀鏇夸唬浜嬩欢鍘嗗彶銆?
鏂板 4 涓?TableModel 鐢ㄤ緥锛?
- `DrawTerminalObservedBeforeLaterEmptyPublicationStillConfirms`锛歵erminal cmd6 鍏堝埌锛岀┖ publication cmd7 鍚庡埌锛孉pplied cmd6 鍚庡埌 鈫?1 confirm / 1 commit銆?- `DrawTerminalObservedAfterIntermediateEmptyPublicationStillConfirms`锛氱┖ cmd6 鍏堝埌锛宼erminal cmd7 鍚庡埌锛孉pplied cmd7 鈫?1 confirm銆?- `LaterDifferentVersionTerminalDoesNotEraseTheEarlierObservedTerminal`锛歵erminal6 鍚庡啀鏉?terminal7锛孉pplied6 浠嶉厤瀵?terminal6銆?- `DrawReceiptForAnUnobservedVersionDoesNotConfirmAnEarlierTerminal`锛氬彧鏈?terminal6锛孉pplied7 鈫?`VISIBLE_EVENT_MISSING`锛? commit/unresolved銆?
**楠岃瘉**锛歍ableModel **235/235**锛汳utation E锛堟妸鍘嗗彶閬嶅巻閫€鍥?`View.Current`锛夊疄闄?**2 failed / 233 passed / 235 total**锛屽け璐ユ鏄墠涓や釜 history-dependent 鐢ㄤ緥锛涙仮澶嶅搱甯屼竴鑷淬€侾layMode attempt-13 **7/7**锛屽寘鎷鍓?attempt-12 澶辫触鐨勫悓涓€ wall-0 request `fm-draw-0ceebb...`銆?
### 11.2 C01-R2-03锛氳寖鍥存仮澶嶏紙宸蹭慨澶嶏級

- 鍒犻櫎 `SessionCoordinator.cs` 涓秺鐣岀殑 `ReconcilePendingAsync` 28 琛岋紱鍒犻櫎 `IFullMatchExecutionTransport.ReconcileAsync`銆乥udget reconcile 瀛楁銆乥oundary `ObserveReconcile`/瀛楁/涓?涓?reconcile 鐢ㄤ緥鍙婁袱 transport 瀹炵幇銆?- 鍒犻櫎瓒婄晫鎶ュ憡 `V5-G2-C01-EXECUTION-BOUNDARY-DELIVERY-R2-20261101.md`锛汻2 鐨勬湁鏁堝唴瀹瑰苟鍏ユ湰鏂囦欢鏈妭銆傚凡鎻愪氦鍘嗗彶鍜?ignored 璇佹嵁淇濈暀銆?- 鏈€缁?`git diff --name-only c3c95af..HEAD` 鍙秹鍙婂師 expectedFiles 鐨?9 涓枃浠讹紙R1 鐨?8 涓唬鐮?娴嬭瘯 + 鏈姤鍛婏級锛沗SessionCoordinator.cs` 涓?R2 鎶ュ憡鍦ㄦ渶缁堟爲涓负闆跺樊寮傘€?- 鏈娇鐢ㄦ棦鏈夊厑璁?API 涔嬪鐨勭敓浜ф墿灞曪紱娌℃湁璇锋眰鏂扮殑 scope 鎺堟潈銆?
### 11.3 C01-R1-02锛氫繚鐣欑殑 full-match 闃绘柇锛堟湭鍏抽棴锛?
**瑙傚療浜嬪疄**锛?
- 鏈€缁堜唬鐮?fresh Player `p45-20261101-013326-201333eb`锛坣ative 43锛宼ool 10锛夊湪 `local-main-r1` 鐨勯涓帺瀹跺己鍒?`DrawTile` 澶辫触锛?  - request `fm-draw-ba0bdf37a073a97425ca380c46b8c24e`
  - baseline 80
  - `command_submission`锛歚sent=true`銆乣handedToTransport=true`銆乣needsReconciliation=true`
  - 30s 鍐呮病鏈?`transport_receipt_seen`銆佹病鏈?`client_receipt_seen`銆佹病鏈?`RoundEnded`锛沗RECEIPT_UNRESOLVED`銆?- R2 鎴愬姛鏍锋湰 `p45-20261101-005817-57a45a02` 淇濈暀锛?2 灞€銆乣matchFinished=true`銆乣WestCompletion`銆乶ative 0/tool 0锛涜鏍锋湰鐨勭帺瀹舵懜鎵撳潎涓烘甯告湁鐗屾懜鎵擄紝鏈鐩栨湰娆?baseline 80 鐨勮矾寰勩€?- PlayMode attempt-13 7/7锛汻2 attempt-12 鐨?6/7 澶辫触璁板綍鍜?XML `1530D961...` 淇濈暀锛屼笉鍒犻櫎銆佷笉閲嶅啓銆?
**鏈瘉瀹炴帹鏂挙鍥?*锛歊2 鎶ュ憡鏇炬妸鈥滄棤 coordinator `ReceiptResolved`鈥濆啓鎴?host 娌℃湁 receipt / wall0 鏍瑰洜銆俁3 涓嶈繖鏍锋柇瑷€銆傚綋鍓嶅彧鑳界‘璁わ細瀹㈡埛绔凡鎻愪氦骞?handoff銆佹寚瀹?30s 鍐呮病鏈?coordinator receipt 浜嬩欢銆佹病鏈?RoundEnded锛?*娌℃湁** host egress/`SendAsync` 杩斿洖/鍘熷 lookup 缁撴灉锛屼篃**娌℃湁**璇佹槑 Client resolver 浠庢湭杩涘叆鎴?wall0 蹇呬负鏍瑰洜銆傛病鏈夋嵁姝ょ紪杈?Client/LocalHost/Domain/Contracts銆?
**鎵€闇€涓嬩竴姝ワ紙caller 瑁佸喅锛?*锛氬湪鍙 probe 涓寘瑁呯湡瀹?`ITableChannel`锛岃褰曞師 canonical 甯с€乭ost `SendAsync` 杩斿洖/寮傚父銆乺aw receipt/lookup 鍝嶅簲銆乷utbox/pump/娲惧彂杈圭晫锛涙垨鎺堟潈鏈€灏?LocalHost draw/settlement 璇婃柇鍏ュ彛銆傚綋鍓嶄笉鑷姩鎵╁眰銆侰01-R1-02 浠嶄负 MAJOR/鏈叧闂€?
### 11.4 R3 鏈€缁堣鏁?
| Suite | Total | Pass | Fail | Skip | native exit | XML SHA-256 |
|---|---:|---:|---:|---:|---:|---|
| TableModel | 235 | 235 | 0 | 0 | 0 | `46D289FB...` |
| TableRuntime | 482 | 482 | 0 | 0 | 0 | `A1270FA2...` |
| LocalHost | 127 | 127 | 0 | 0 | 0 | `ACBE4E2C...` |
| Tool | 32 | 32 | 0 | 0 | 0 | log `6745E63F...` |
| PlayMode attempt-13 | 7 | 7 | 0 | 0 | 0 | `CD0BF2C8...` |
| Player R2 success sample | 鈥?| PASS | 0 | 0 | 0 / 0 | summary `6B99801B...` |
| Player R3 final | 鈥?| **FAIL** | 1 | 0 | native 43 / 10 | summary `8DAA9944...` |

- R3 浠ｇ爜/娴嬭瘯鎻愪氦锛歚077c273b10f5c6d730be0afe2515781627e9f8c0`
- R3 鎶ュ憡鎻愪氦锛堟渶缁?HEAD锛夛細鏈枃浠舵墍鍦ㄦ彁浜?- `git status --porcelain` 绌猴紱`git diff --check base..HEAD` 0锛涙渶缁堝樊寮?9 涓師 expectedFiles銆?- 鏈?push/PR/MR/merge/deploy/scheduler/complete.flag锛涙湭鑷鎵瑰噯銆?

