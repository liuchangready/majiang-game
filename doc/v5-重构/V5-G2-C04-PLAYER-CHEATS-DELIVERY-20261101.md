# V5 G2-C04 涓夌粷鎶€鍥哄畾鐪熷疄 Player 涓撻」浜や粯锛?026-11-01锛?
## 1. 缁戝畾涓庡喕缁撹寖鍥?
| 椤?| 鍊?|
|---|---|
| acceptedBase | `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` |
| frozenCandidate | `f3ba015b7b54061a0b144ddf546cd6f8e37548ca` |
| branch | `wt/v5-g2-c04-player-cheats-ds-20261101-1545` |
| 浠诲姟 ID | G2-C04-D04 / 鍥哄畾 C04-G3-01..06 |
| Unity / .NET | `6000.3.27f1` / `D:\Unity\dotnet`锛?.0.32锛?|
| 璇佹嵁鏍?| 鏈爲 ignored `build/c04-player-cheats-final-20261101-171354` |
| 缁撹涓婇檺 | 鏈枃浠跺彧鏄湰鍙楁祴鍊欓€夌殑 C04-G3 涓撻」璇佹嵁锛?*涓嶆槸姝ｅ紡 C04/G2 閫氳繃**锛涙仮澶?鍔ㄤ綔澧為噺鍚堝洖鍚庡繀椤婚噸鏂扮粦瀹氭渶缁堢増鏈?|

宸ヤ綔鏍戜粠 acceptedBase 鍒涘缓骞?ff 鍒?frozenCandidate锛涘紑濮嬪墠 `git status --porcelain` 涓虹┖銆乣git diff --check` 涓?0銆乤cceptedBase 涓?HEAD 绁栧厛銆傚疄鐜版湡闂村彧鏂板鏈换鍔?13 涓巿鏉冩枃浠讹紱Unity/鍏变韩 DLL 鏋勫缓浜х敓鐨?tracked 鎻掍欢銆佸瓧浣撱€丼ettings銆丳rojectSettings銆乻lnx 鍜?PerformanceTestRun 鐢熸垚鐗╁凡鎸夌簿纭矾寰勬仮澶嶅埌 HEAD锛屾湭鏁寸洰褰?clean/restore銆?
## 2. 瀹炵幇鏂囦欢锛堥€愭枃浠讹級

| 鏂囦欢 | 浣滅敤 |
|---|---|
| `dotnet/src/Mahjong.LocalHost/LocalPlayerCheatScenario.cs` | 鍞竴绾?.NET 鍙楁帶鍒濇€侊細鍥哄畾 136 瀹炰綋 0..135銆佸浐瀹氬簞瀹舵墜鐗?鐗屽北銆?20 鍒濆鐐规暟銆?/10/10 璐圭敤锛涘彧鏆撮湶鏋氫妇閫夋嫨鐨?`CreateOptions` 涓庡彧璇?facts锛屼笉鎺ュ彈璋冪敤鏂规瀯閫犵殑杞?鐗?璐︽湰 |
| `dotnet/tests/Mahjong.LocalHost.Tests/C04PlayerCheatChannelTests.cs` | 4 涓?.NET 鐢ㄤ緥锛氬満鏅?136銆佷笁缁濇妧鐪熷疄 TableSession/LocalTableChannel/鏉冨▉銆佺己澶?decision 鎺堟潈鎷掔粷闆跺閲忋€佷綑棰濅笉瓒虫嫆缁濋浂澧為噺 |
| `unity/.../App/C04PlayerCheatChannelFactory.cs` | 鐙珛 `ITableChannelFactory`锛涘祵濂楅€氶亾瀹炵幇 `ILocalTableTicketSource`锛岄€氳繃 AppComposition 鐜版湁 channelFactory 娉ㄥ叆鐪熷疄 LocalTableHost |
| `unity/.../App/C04PlayerCheatProbe.cs` | 涓ユ牸鍛戒护琛?opt-in Windows Player probe锛涚湡瀹?TablePresenter 鎸夐挳/鎰忓浘 鈫?ITableCommandSink 鈫?TableSession 鈫?LocalTableChannel锛涘彧璇?LocalTableHost recovery capture 璁板綍 136/ledger/request 浜嬪疄 |
| `unity/.../Editor/C04PlayerCheatBuild.cs` | 浠?Editor 鐨?Windows64 鏋勫缓鍏ュ彛锛涘啓 build report锛屼笉鏀硅缃?璧勪骇 |
| `unity/.../Tests/PlayMode/C04PlayerCheatScenarioTests.cs` | PlayMode 鐪熷疄 LocalHost + 鐪熷疄 TablePresenter 缁勪欢绾ч┍鍔ㄤ笌鎺堟潈鎷掔粷鐢ㄤ緥 |
| `tools/unity/run-p45-c04-player-cheats.ps1` | 鎸?`Local\MahjongV5P45UnityValidation`锛涙嫆缁濊鍐欒瘉鎹牴锛沚uild-shared/provenance guard/Unity build/Player/native exit/楠岃瘉/manifest/summary |
| `tools/tests/C04PlayerCheats.Tests.ps1` | `SCRIPT_TOOL_FIXTURE` 11 椤硅礋渚嬶細缂?case/缂?receipt/閿欒韩浠?鍝堝笇涓嶇/闈為浂 native/瓒呮椂/绌烘柇瑷€/鏈煡 blocked/瑕嗗啓鎷掔粷 |
| 鍥涗釜鏂?`.meta` | Unity 鑷姩鐢熸垚骞舵牳楠屽叏灞€鍞竴 GUID |
| 鏈姤鍛?| 浜や粯涓庨€愯缁撹 |

## 3. 瀹為檯鎵ц涓庤鏁?
### 3.1 .NET锛圖OTNET_PURE锛?
鍛戒护锛?
```powershell
$env:DOTNET_ROOT='D:\Unity\dotnet'
dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.exe `
  -class Mahjong.LocalHost.Tests.C04PlayerCheatChannelTests `
  -result-xml build/c04-player-cheats-final-20261101-171354/dotnet-c04.xml
```

缁撴灉锛?*Total 4 / Passed 4 / Failed 0 / Skipped 0 / native 0**锛?.780s銆傚師 XML `66959BEBEC8CC3D0C0EDED0B45AEEB0A999ED20FD8468EA5F2E20B775F7435CC`锛沜onsole `AE9A1CFC979170CAAD24C12ABA8A16A24A1B0A43B4C16D42296C7F6AF6A88F77`銆?
瑕嗙洊浜嬪疄锛?
- `AuthorizedThreeCheatsUseTheRealChannelWithExactFeesAnd136Entities`锛歍ileMark 鈭?锛?19锛夈€丼leeveBloom 鈭?0锛?09锛夈€丅eamSwap 鈭?0锛?9锛夛紱3 鏉＄鏈?ledger锛涗笁娆?136/136锛沜hoice 涓嶉噸澶嶆墸璐癸紱SleeveBloom SECOND/BeamSwap SWAP 閫犳垚鐗╃悊鍝堝笇鍙樺寲锛涙寔涔呭寲 request 鐨?raw payload hash 涓?receipt 鍖归厤銆?- `MissingDecisionAuthorizationIsRejectedByTheRealAuthorityWithZeroDelta`锛氶敊璇?decision 韬唤鍦ㄦ潈濞佸 Rejected锛泂tateVersion/eventSeq/cheatVersion/cheatFingerprint/tiles hash/ledger entries/balances 鍏ㄩ儴闆跺閲忋€?- `InsufficientBalanceIsRejectedByTheRealAuthorityWithZeroDelta`锛?2 娆″悎娉曟墸璐瑰悗浣欓 0锛屼笅涓€娆?SleeveBloom 琚潈濞?Rejected锛坮eason 鍚?`INSUFFICIENT`锛夛紱闆剁増鏈?浜嬩欢/ledger/瀹炰綋澧為噺銆?- `ScenarioDealsAll136UniqueEntitiesAndPinsTheAcceptedEconomy`锛?36 鍞竴瀹炰綋 0..135锛岃垂鐢?鍒濆鐐规暟鍥哄畾銆?
### 3.2 PLAY MODE锛圲NITY_PLAYMODE锛岀粍浠跺眰锛屼笉鏄?Player锛?
鍛戒护锛?
```powershell
Unity.exe -batchmode -projectPath unity/Mahjong.Unity `
  -runTests -testPlatform PlayMode `
  -testFilter Mahjong.Unity.Tests.PlayMode.C04PlayerCheatScenarioTests `
  -testResults build/dev-27bde11b520945fabd7f2a8660ea1738/playmode-c04e.xml
```

缁撴灉锛?*Total 2 / Passed 1 / Failed 1 / Skipped 0 / runner exit 2**銆傚師浠?`build/dev-27bde11b520945fabd7f2a8660ea1738/playmode-c04e.xml`锛堟渶缁堝畾鍚戣繍琛岋級锛涘巻鍙查涓け璐ュ師浠?`playmode-c04.xml`/`c04b.xml`/`c04d.xml` 淇濈暀銆?
- `UnauthorizedDecisionReceiptIsRejectedWithoutVisibleDelta`锛歅ASS锛涚湡瀹?sink 鍙戦€侀敊璇?decision锛宎uthority Rejected锛屽彲瑙佺増鏈?鐐规暟/ledger 涓嶅彉銆?- `AuthorizedThreeCheatsThroughPresenterWithExactPrivateLedger`锛欶AIL锛涢杞?TileMark 鍏ュ彛缂哄け锛坄PrivateCheatView.TileMark == null`銆乣TileMarkButton` inactive锛夛紝闅忓悗 SleeveBloom 琚?stale `WindowId` 璇垽涓?off-turn锛屾敹涓嶅埌 `SubmitSleeveBloom` 鍥炴墽銆傝繖鏄?PlayMode 澶嶇幇鐨勪骇鍝佸叆鍙ｇ己闄凤紝涓嶆槸娴嬭瘯鍋囩豢锛涙病鏈?skip/鏀惧鏂█銆?
### 3.3 WINDOWS PLAYER锛堢湡瀹?GAME锛屾湰鍊欓€夛級

鏋勫缓锛歚C04PlayerCheatBuild.Build`锛宍result=Succeeded`锛孭layer 128,277,084 bytes锛沗totalErrors=3` 涓?BuildReport 闈炶嚧鍛介敊璇鏁帮紝鏃ュ織鍘熶欢淇濈暀锛屼笉鑳界О涓?0 error 鏋勫缓銆傚叡浜?DLL 鐢ㄦ湰浠诲姟 final `LocalPlayerCheatScenario` 閲嶆柊 build-shared锛宲rovenance 151 inputs / 19 outputs锛沗check-shared-plugins` 閫氳繃鍚庢墠鍏佽 Player 鏋勫缓銆?
Runner锛?
```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File tools/unity/run-p45-c04-player-cheats.ps1 `
  -Case authorized -EvidenceRoot build/.../player-authorized `
  -PlayerPath build/dev-.../player/Mahjong.exe `
  -SourceHead f3ba015b7b54061a0b144ddf546cd6f8e37548ca `
  -BuildCommit local-dev -SkipBuild
```

缁撴灉锛?*native 1 / status FAIL / errors = NATIVE_EXIT_1, MISSING_RAW_HASH 脳2**锛?1 assertions锛? pass / 2 fail銆?
- `G3-03-RECEIPT` PASS锛歅layer TablePresenter 鍙戝嚭 `SubmitBeamSwap`锛屾潈濞?Applied锛岀偣鏁?120鈫?10锛宭edger `-10@110/BEAM_SWAP`锛?36/136 瀹堟亽銆?- `G3-03-OFFER` PASS锛? 涓湡瀹炲鍊欓€夈€?3 涓彲閫夋墜鐗屻€?- `G3-04-BEAM-FEE` PASS銆乣G3-05-AFTER-BEAM` PASS銆?- `G3-03-CHOICE-RECEIPT` FAIL锛歅layer 鐨?SWAP choice 琚潈濞?`REJECTED/MALFORMED_MESSAGE`锛沗G3-03-CHOICE` 瓒呮椂锛屽疄浣撴湭鍙戠敓 swap銆?*涓嶈兘澹扮О鍋锋鎹㈡煴鎴愬姛銆?*
- `G3-01-UI-ENTRY` PASS锛堝鐜板け璐ユ潯浠讹級锛氶杞?`tileMark=null`銆乣TileMarkButton.active=False`銆?- `G3-02-UI-ENTRY` PASS锛堝鐜板け璐ユ潯浠讹級锛氱浜岃疆 `window=bloom-window-0-0-0`銆乮mmediate offer 涓虹┖锛汸resenter 鍥犳閫夋嫨 off-turn锛宨mmediate 璺緞涓嶅彲杈俱€?
璇佹嵁鍘熶欢锛歚player-authorized/authorized/case-authorized.json` SHA `BEE118512C2AF52C3E2085FA3F305566172F299D22F696CA62D275B5F53B0B68`锛沗player.log` `0EE5F09627787AEC908D296987C0BF4297522BA26909836F97FA0A57E4D55520`锛沗c04-g3-cheats.save` `E3C559B4B2447D503EC7D66AF44AC41C72AD0CEB5F51F2375D4C2C43BD154008`锛沵anifest `A5D24DD40B8609563868EB6E8AF66F74818E8F59950983D8864DE2785EAE6989`锛泂ummary `D58015013CC210B9235845F748A7F18C81B59F361DFE675754F429890310C091`銆?
### 3.4 SCRIPT_TOOL_FIXTURE

`tools/tests/C04PlayerCheats.Tests.ps1`锛?*passed=11 failed=0**锛宯ative 0銆傛棩蹇?`9F7DEFC74BBDFC83C25435E84FDFFD099EDF5D44C9F022B14F2437F12E9F8D0C`銆傝灞傚彧璇佹槑 verifier/鎷掔粷瑕嗗啓/缂?receipt/閿欒韩浠?鍝堝笇/闈為浂 native/瓒呮椂锛屼笉浠ｈ〃浠讳綍 Player 娓告垙閫氳繃銆?
## 4. C04-G3 鍥哄畾琛岀粨璁猴紙鏈彈娴嬪€欓€夛級

| 琛?| 缁撹 | 渚濇嵁/缂哄彛 |
|---|---|---|
| C04-G3-01 钀芥睏鎴愬姛 | **BLOCKED** | 棣栬疆 `PrivateCheatView.TileMark == null`锛沗TableProjectionContractMapper.MapOwnerCheat` 鍦?`MarkGroups.Count == 0` 鏃惰繑鍥?null锛沗TableCommandIntentFactory.CreateSubmitTileMark` 鎶?`TILE_MARK_UNAVAILABLE`銆?NET 鏉冨▉璺緞 4/4 涓?TileMark 鎴愬姛锛屼絾涓嶆槸 Player UI 璇佹嵁 |
| C04-G3-02 琚栭噷钘忚姳鎴愬姛 | **BLOCKED** | 寮冪墝鍚?`WindowId` 浠嶉潪绌猴紱`TablePresenter.OpenSleeveBloomDialog` 鍙 `WindowId` 闈炵┖灏遍€?`SubmitSleeveBloomOffTurn`锛屽湪鑷繁鍥炲悎琚潈濞佹嫆缁濓紱immediate 璺緞涓嶅彲杈?|
| C04-G3-03 鍋锋鎹㈡煴鎴愬姛 | **FAIL/PARTIAL** | 婵€娲?Applied銆佽垂鐢?offer/136 鍧?PASS锛汼WAP choice 琚?`MALFORMED_MESSAGE` 鎷掔粷锛屾棤瀹炰綋浜ゆ崲銆?NET 缁勪欢灞傚悓 payload 鐨?SWAP 閫氳繃锛屽樊寮傚湪 Player presenter/command payload |
| C04-G3-04 绮剧‘璐圭敤/绉佹湁 ledger | **PARTIAL PASS** | Player BeamSwap 婵€娲?鈭?0銆乥alance 110銆佷竴鏉＄鏈?ledger锛汿ileMark/SleeveBloom 鏈兘鍦?Player 鎵ц锛屼笉鑳界粰涓夌瑪瀹屾暣缁撹 |
| C04-G3-05 136 鐗╃悊瀹堟亽 | **PASS锛堟湰鍊欓€?Player + .NET锛?* | Player before/after BeamSwap 鍧?136/136锛?NET 涓夊姩浣滃潎 136/136锛泂ave/tiles SHA 淇濈暀 |
| C04-G3-06 鎺堟潈缂哄け/浣欓涓嶈冻鎷掔粷 | **.NET PASS / PLAYER NOT_RUN** | .NET 涓ょ嫭绔?request 鍧?Rejected 涓旈浂澧為噺锛汸layer 渚у洜 G3-01/G3-02 鍏ュ彛闃绘柇鏃犳硶鐢ㄥ悓涓€ UI 瀹屾垚 spend-down锛屾湭浼€?Player 缁撴灉 |

## 5. 鏈疆瀹屾暣缂洪櫡娓呭崟锛堜竴娆＄粰鍏級

### C04-G3-R1-01 BLOCKER 鈥?棣栦釜钀芥睏鍦?Player UI 涓嶅彲杈?
- 浣嶇疆锛歚unity/Mahjong.Unity/Assets/Scripts/Table/TablePresenter.cs:1234`锛堟寜閽粎鍦?`cheat.TileMark != null` 鏄剧ず锛夈€乣TableCommandIntentFactory.cs:240-241`銆乣dotnet/src/Mahjong.TableRuntime/TableProjectionContractMapper.cs:377`銆?- 澶嶇幇锛歅layer `-c04-player-cheats-case authorized`锛屾垨 PlayMode `AuthorizedThreeCheats...`锛涜瀵?`LatestView.Own.Cheat.TileMark == null`銆乣TileMarkButton.activeSelf == false`銆?- 瀹為檯锛氶杞棤娉曟墦寮€/纭钀芥睏锛沗CreateSubmitTileMark` 鎶?`TILE_MARK_UNAVAILABLE`銆?- 鏈熸湜锛氬湪鏈夊悎娉?decision 鐨勮嚜宸辩殑鍥炲悎锛屽嵆浣垮皻鏃?mark group锛屼篃搴旀姇褰卞彲閫?4 寮犳墜鐗屽苟鍏佽棣栨鎻愪氦銆?- 璇佹嵁锛歚case-authorized.json` 涓?`G3-01-UI-ENTRY` actual `tileMark=null buttonActive=False`锛汸layMode XML `playmode-c04e.xml`銆?- 淇瑕佹眰锛氭姇褰卞眰鍦?`MarkGroups.Count == 0` 鏃朵粛鎻愪緵 owner `TileMark`锛堝惈 selectable hand IDs銆乺oundId锛夛紝Presenter 浠呭湪鐪熸鏃?decision/鏃犲€欓€夋椂闅愯棌鎸夐挳锛涗笉寰楃敤 probe 鐩存帴鎻愪氦鎴栨墜鍐?view 缁曡繃銆?- 楠屾敹鏂█锛氬悓涓€ Player/PlayMode 璺緞棣栨 mark 寰楀埌 Applied銆佲垝1銆?19銆佷竴涓鏈?group銆?36 涓嶅彉锛涢敊璇?seat/epoch/round 浠?Rejected 闆跺閲忋€?
### C04-G3-R1-02 BLOCKER 鈥?琚栭噷钘忚姳 immediate 璺緞琚?stale off-turn window 鎶㈠崰

- 浣嶇疆锛歚TablePresenter.cs:1856-1880`锛坄isOffTurn = !string.IsNullOrEmpty(cheat.WindowId)`锛夈€乣dotnet/src/Mahjong.TableRuntime/TableActor.cs:1630-1636`锛堝純鐗屽紑绐楋級銆乣SleeveBloom.WithClearedWindow` 浠呭湪 reservation delivery 浣跨敤銆?- 澶嶇幇锛歅layer 棣栬疆寮冨寳鍚庢帹杩涘埌搴勫涓嬩竴鎽革紱瑙傚療 `window=bloom-window-0-0-0`銆乣ImmediateSleeveBloom == null`锛涚偣鍑?`SleeveBloomButton` 瀹為檯鍙戝嚭 `SubmitSleeveBloomOffTurn`銆?- 瀹為檯锛歩mmediate `SubmitSleeveBloom` 鏃犳硶鍙戝嚭锛沷ff-turn 鍦ㄨ嚜宸卞洖鍚堣鏉冨▉鎷掔粷锛汸layMode 鏀朵笉鍒?`SubmitSleeveBloom` 鍥炴墽銆?- 鏈熸湜锛氳嚜宸卞洖鍚堜笖 `DrawnTile` 灞炰簬鑷繁鏃跺簲璧?immediate锛沷ff-turn window 鍦ㄦ墍鏈夎€呬笅涓€娆℃懜鐗?鍥炲悎杈圭晫鍏抽棴鎴栦笉鍐嶈褰撲綔 immediate 鐨勬浛浠ｃ€?- 璇佹嵁锛歚case-authorized.json` 鐨?`G3-02-UI-ENTRY` actual `window=bloom-window-0-0-0 offer=False`锛汸layMode 澶辫触鍘熶欢銆?- 淇瑕佹眰锛氭槑纭?window 鐢熷懡鍛ㄦ湡骞跺湪 Presenter 閫夋嫨璺緞鏃跺悓鏃舵牎楠?current seat/phase锛涚姝㈢敤 `KeepOriginal` 鎴栦粎鍒涘缓 offer 鍐掑厖鎹㈢墝鎴愬姛銆?- 楠屾敹鏂█锛歅layer immediate 婵€娲?鈭?0銆?09銆乷ffer锛孲ECOND 鍚庡疄浣撳搱甯屽彉鍖栵紱off-turn 浠呭湪鐪熸浠栧鍥炲悎鍙彁浜ゅ苟鏈?reservation delivery 璇佹嵁銆?
### C04-G3-R1-03 BLOCKER 鈥?Player BeamSwap SWAP choice 琚?MALFORMED_MESSAGE 鎷掔粷

- 浣嶇疆锛歚TablePresenter.OnConfirmBeamSwapClicked` 鈫?`TableCommandIntentFactory.CreateSubmitBeamSwapChoice` 鈫?`CommandPayloadFactory.SubmitBeamSwapChoice`锛涙潈濞?`LocalTableRuntime.SubmitCheatAsync` 瑙ｆ瀽 `HandTileId`/`TargetSlot`銆?- 澶嶇幇锛歅layer authorized 鍦烘櫙锛孊eamSwap 婵€娲?Applied 鍚庨€?`BeamSlot_0` + 涓€鎵嬬墝 + Confirm锛況eceipt `REJECTED/MALFORMED_MESSAGE`銆?- 瀹為檯锛歚SubmitBeamSwapChoice` payload 琚潈濞佸垽 malformed锛宱ffer 鏈竻闄わ紝鏃犲疄浣撲氦鎹€?- 鏈熸湜锛氬悎娉?SWAP 寰楀埌 Applied锛宍BeamSwapResolved(SWAP)`锛屾墜鐗?澧欏疄浣撲簰鎹紝136 涓嶅彉銆?- 璇佹嵁锛歚case-authorized.json` request `57f4d510...` status `REJECTED/MALFORMED_MESSAGE`锛宲ayload SHA `3599B1EA...`锛汸layer log銆?- 淇瑕佹眰锛氬姣?Player presenter payload 涓?.NET 宸查€氳繃鐨勫悓鍚?payload 瀛楄妭锛涗慨姝ｅ彲閫夊瓧娈靛啓鍏?瑙ｆ瀽锛堜笉寰楁斁瀹?verifier锛夛紱鑻ラ渶鏀瑰叡浜?Contracts/Tableruntime锛屽厛鎻愪氦鏈€灏忔枃浠惰姹傦紝涓嶅湪鏈换鍔℃墿灞傘€?- 楠屾敹鏂█锛氬悓涓€ Player 璺緞 SWAP Applied锛沜hoice 涓嶉噸澶嶆墸璐癸紱瀹炰綋鍝堝笇鍙樺寲锛涢敊璇?slot/hand 浠?Rejected 闆跺閲忋€?
### C04-G3-R1-04 MAJOR 鈥?Player 璇佹嵁缂?raw payload hash

- 浣嶇疆锛歚C04PlayerCheatProbe.CheckRequestApplied/Rejected` 涓?evidence request 璁板綍銆?- 澶嶇幇锛歳unner 楠岃瘉 `case-authorized.json` 鎶?`MISSING_RAW_HASH 脳2`銆?- 瀹為檯锛氫袱鏉?request 鐨?`rawPayloadSha256` 涓虹┖锛屽彧鏈?canonical digest銆?- 鏈熸湜锛氭瘡鏉℃垚鍔?鎷掔粷 request 閮芥湁 canonical digest 涓?64-hex raw payload hash锛屽苟涓庢潈濞?request fact 涓€鑷淬€?- 璇佹嵁锛歚runner-authorized.log`锛沗case-authorized.json`銆?- 淇瑕佹眰锛氬湪 receipt 钀借处鏃朵粠鏉冨▉ request fact 鎴栧師濮?canonical bytes 濉?raw hash锛岀姝㈢敤绌哄€?杩戜技鍊奸€氳繃銆?- 楠屾敹鏂█锛歷erifier 瀵规瘡鏉?request 瑕佹眰 64-hex raw hash 涓旂瓑浜庢潈濞?fact锛涚己澶卞嵆 FAIL銆?
### C04-G3-R1-05 MAJOR 鈥?瀹氬悜 PlayMode 澶辫触涓?G3-06 Player 鏈繍琛?
- 浣嶇疆锛歚C04PlayerCheatScenarioTests.AuthorizedThreeCheats...`锛汸layer negative 鏈墽琛屻€?- 澶嶇幇锛歅layMode 鍛戒护 native 2锛? passed / 1 failed锛汸layer runner 鍙窇 authorized銆?- 瀹為檯锛歅layMode 鍦?TileMark/SleeveBloom 鍏ュ彛澶辫触锛沶egative Player 涓?NOT_RUN銆?- 鏈熸湜锛氫袱涓?PlayMode 鐢ㄤ緥鍧?PASS锛汸layer authorized + negative 鍧囨湁 native 閫€鍑轰笌閫愯缁撹銆?- 璇佹嵁锛歚playmode-c04e.xml`锛沗runner-authorized.log`銆?- 淇瑕佹眰锛氬厛鍏抽棴 R1-01/R1-02锛屽啀閲嶈窇 PlayMode 鍜?negative锛涗笉寰楄烦杩囧け璐ョ敤渚嬫垨鐢ㄧ粍浠惰瘉鎹唬鏇裤€?- 楠屾敹鏂█锛歅layMode Total 2 / 2 PASS / native 0锛汸layer authorized 涓?negative 鍧?native 0 涓旈€?request/ledger/136 鏂█閫氳繃銆?
### C04-G3-R1-06 MINOR 鈥?BuildReport errors=3 涓庢垚鍔熺粨鏋滃苟瀛?
- 浣嶇疆锛歚C04PlayerCheatBuild.Build` 鎶ュ憡涓?runner銆?- 澶嶇幇锛歅layer build report `result=Succeeded, errors=3`銆?- 瀹為檯锛歅layer 鍙惎鍔ㄥ苟浜х敓璇佹嵁锛屼絾鎶ュ憡涓嶆槸 0 error銆?- 鏈熸湜锛氳涔堣В閲婂苟娑堥櫎 3 涓潪鑷村懡 error锛岃涔堝湪 summary/manifest 涓崟鍒?`BUILD_REPORT_ERRORS=3`锛屼笉寰楀啓鎴?0 error銆?- 璇佹嵁锛歚player-build2.json` / `player-build2.log`銆?- 淇瑕佹眰锛氫繚鐣欏師浠讹紱build script 璁板綍 errors 鍘熷€硷紝runner 鏄庣‘鏄剧ず銆?- 楠屾敹鏂█锛氭渶缁?build report `errors=0`锛屾垨鎶ュ憡涓?summary 鍧囨樉寮忚褰曢潪闆跺苟璇存槑鍘熷洜銆?
## 6. 宸茬‘璁ょ殑姝ｅ悜/杈圭晫璇佹嵁

- 鍙楁帶鍒濇€侊細`LocalPlayerCheatScenario` 鍥哄畾 14 寮犲簞瀹舵墜鐗屼笌鎸夋Ы浣嶅浐瀹氱殑 136 瀹炰綋 0..135锛涙病鏈?private reflection銆佹病鏈夐鍐?receipt/event/ledger銆?- 璐圭敤锛氭湰鍊欓€夊疄闄呴厤缃?`TILE_MARK=1, SLEEVE_BLOOM=10, BEAM_SWAP=10, initial=120`锛?NET 绮剧‘鏂█ 119/109/99 涓?3 鏉?ledger锛汸layer BeamSwap 鏂█ 鈭?0/110銆?- 136锛歅layer 鍓嶅悗 recovery capture 鍧?`TileCount=136/Unique=136`锛泂ave SHA 涓?tiles SHA 淇濈暀銆?- 鎷掔粷锛?NET unauthorized/insufficient 鍧?Rejected 涓?stateVersion/eventSeq/cheatVersion/cheatFingerprint/tiles/ledger/balances 闆跺閲忥紱涓?request ID/payload 涓嶅悓銆?- opt-in锛氭棤 `-c04-player-cheats` 鏃?`Bootstrap` 鍦ㄥ垱寤?GameObject/鐩綍/host/绾跨▼鍓嶈繑鍥烇紱宸ュ叿娴嬭瘯楠岃瘉 runner 鎷掔粷瑕嗗啓宸插瓨鍦ㄨ瘉鎹牴銆?- 娓呯悊锛歅layer 鍙啓鏈爲 ignored `build/<GUID>`锛沀nity 鐢熸垚鐨?tracked 鎻掍欢/璁剧疆/瀛椾綋/slnx 宸叉寜绮剧‘璺緞鎭㈠ HEAD锛屾湭鍒犲巻鍙茶瘉鎹?鐢ㄦ埛鍘熶欢銆?
## 7. 鏈€灏忓繀瑕佹枃浠惰姹傦紙涓嶆搮鑷墿灞傦級

瑕佸叧闂笂杩?BLOCKER锛岄渶瑕佸彟琛屾巿鏉冪殑鏈€灏忚寖鍥达細

1. `unity/Mahjong.Unity/Assets/Scripts/Table/TablePresenter.cs`锛氶 mark 鍏ュ彛銆乮mmediate/off-turn window 閫夋嫨銆丼WAP choice 鎻愪氦銆?2. `dotnet/src/Mahjong.TableRuntime/TableProjectionContractMapper.cs`锛堝強蹇呰鐨?`TableVisibilityProjection.cs`锛夛細棣栦釜 `TileMarkView` 鐨?selectable hand IDs銆?3. 鑻?R1-03 璇佹槑鏄?wire 鍚堝悓鑰岄潪 presenter锛歚dotnet/src/Mahjong.Contracts/ProtocolCommand.cs` / `LocalTableRuntime.SubmitCheatAsync` 鐨勬渶灏忚В鏋愪慨澶嶃€?
鏈换鍔℃湭淇敼浠ヤ笂浠讳綍鏂囦欢锛屼篃鏈慨鏀?Luna 鐨?`LocalTableChannelFactory.cs`/`LocalPlayerActionScenario.cs`/鍔ㄤ綔 runner/娴嬭瘯鎴栨仮澶?D01 鏂囦欢銆?
## 8. 鍘嗗彶/骞冲彴杈圭晫

- 宸叉湁 C01 12 rounds銆丆02 8 GAME銆丆03 2,820/2,820銆乁I15 绛夊潎涓哄巻鍙茶瘉鎹紱鏈疆鏈噸璺戯紝涓嶆嵁姝ゅ０绉版柊杩愯銆?- 鏈疆鍙墽琛?4 涓?.NET 瀹氬悜鐢ㄤ緥銆? 涓?PlayMode 瀹氬悜鐢ㄤ緥銆? 娆?Windows Player authorized probe 鍜?11 椤瑰伐鍏疯礋渚嬶紱鏈繍琛?Android/iOS/PG/闅忔満鏁村満/姝ｅ紡 C04 鐭╅樀銆?- PR20 淇濇寔 OPEN/Draft锛涗笉 push銆佷笉鏀?PR銆佷笉 record MR銆佷笉 approve銆佷笉鍚堝苟 v5銆佷笉閮ㄧ讲銆佷笉鏀?scheduler/complete.flag銆?- 鏈疆缁撹浠呭鏈彈娴嬪€欓€?`f3ba015b` + 鏈换鍔℃柊澧炴枃浠堕棴鍖呮湁鏁堬紱鎭㈠/鍔ㄤ綔澧為噺鍚堝洖鍚庨渶閲嶆柊缁戝畾骞堕噸璺戙€?
## 9. R1 杩斾慨鏄犲皠涓庨噸璺戯紙2026-11-01锛岃拷鍔狅級

鏈疆鎸?caller 鎺堜簣鐨?18 璺緞鑼冨洿淇骞堕噸璺戯紱鏃?R1 鍘熶欢鍏ㄩ儴淇濈暀锛屼笉鍒犻櫎銆佷笉鍒锋柊銆?
### 9.1 浠ｇ爜淇鏄犲皠

| 缂洪櫡 | 淇 | 鏂囦欢 |
|---|---|---|
| R1-01 棣栨钀芥睏鏃犲叆鍙?| `MapPlayerSnapshot` 灏?owner 鐪熷疄 `ClosedTiles + DrawnTileId`锛堟渶澶?14锛変紶鍏?`MapOwnerCheat`锛沗MarkGroups.Count == 0` 鏃朵粛鐢熸垚 `TileMarkView(selectable, empty groups)`锛沗MapTileMark` 涓嶅啀杩斿洖绌?selectable銆備繚鐣?owner/seat/decision/epoch 鏍￠獙 | `dotnet/src/Mahjong.TableRuntime/TableProjectionContractMapper.cs` |
| R1-02 娈嬬暀 WindowId 璧?off-turn | `OpenSleeveBloomDialog` 鍏堟寜 ownTurn=`currentSeat==identity.seat && phase==CheatWindow && DrawnTileId != null` 鍒ゅ畾锛涗粎 `!ownTurn` 涓?WindowId 闈炵┖鎵嶈蛋 off-turn銆傛湭鏀?Actor/Domain 绐楀彛鐘舵€?| `unity/.../Table/TablePresenter.cs` |
| R1-03 choice 涓?round | `CreateSubmitSleeveBloomChoice`銆乣CreateSubmitSleeveBloomOffTurnChoice`銆乣CreateSubmitBeamSwapChoice` 鐨?`CreateCommand` round 浠?`string.Empty` 鏀逛负 `view.Identity.RoundId`锛涗笉鏀惧鏈嶅姟绔┖ round | `unity/.../Table/TableCommandIntentFactory.cs` |
| R1-03 娴嬭瘯鍚堝悓 | `TableCommandIntentFactoryTests` 涓変釜 choice 鐨?RoundId 鏈熸湜鐢?false 鏀逛负 true | `dotnet/tests/Mahjong.Unity.TableModel.Tests/TableCommandIntentFactoryTests.cs` |
| R1-05 probe 姝ｄ緥 | 鏂板 `RunAuthorizedCaseV2`锛氱湡瀹?Player UI 渚濇 TileMark锛堚垝1/119锛夈€丼leeveBloom锛堚垝10/109/SECOND锛夈€丅eamSwap锛堚垝10/99/SWAP锛夛紝閫?request/ledger/136 鏂█锛涗笉鍐嶆妸缂哄姛鑳藉啓鎴?PASS | `unity/.../App/C04PlayerCheatProbe.cs` |

### 9.2 瀹為檯閲嶈窇

- .NET LocalHost C04锛?*4/4 PASS锛宯ative 0**锛?.8s锛? 鐢ㄤ緥锛夈€?- .NET TableModel锛堝惈淇敼鍚庣殑 3 涓?choice RoundId 鍚堝悓锛夛細**235/235 PASS锛宯ative 0**銆?- .NET TableRuntime 鍥炲綊锛?*483/483 PASS锛宯ative 0**銆?- Unity 缂栬瘧锛歚exit 0`锛屾棤 `error CS`銆?- 鍏变韩 DLL锛歜uild-shared 151 inputs / 19 outputs锛宲rovenance 閫氳繃鍚庢瀯寤恒€?- Player build锛歯ative **0**锛宍result=Succeeded`锛宍errors=3`锛圔uildReport 闈為浂璁℃暟浠嶄繚鐣欏苟鍗曞垪锛屼笉鑳界О 0 error锛夛紝Player 128,286,812 bytes銆?- Windows Player 鐪熷疄杩愯涓ゆ锛堝悓涓€ final 闂寘锛夛細
  1. `build/rework-180546/evidence/authorized`锛堜慨澶?round 鍚庛€佹棫 V1 璺緞锛夛細BeamSwap 涓?SWAP choice 鍧?**APPLIED**锛宺aw hash 闈炵┖锛?5/16 鏂█ PASS锛屽敮涓€澶辫触鏄棫 V1 鐨?`G3-01-UI-ENTRY`锛堣 run 鏈潵涓嶆墽琛?mark锛夈€?  2. `build/rework-180546/evidence2/authorized`锛圴2 璺緞锛夛細TileMark **APPLIED**銆?19銆佲垝1銆佺鏈?group锛汼leeveBloom **APPLIED**銆?09銆乧hoice APPLIED銆丼ECOND 瀹炰綋浜ゆ崲锛?3/14 鏂█ PASS锛屽敮涓€澶辫触涓?SleeveBloom 鍚庣殑绗簩娆?discard 瓒呰繃 15s 鏈帹杩涳紝鏈繘鍏?BeamSwap銆傚師濮?JSON/log 淇濈暀銆?
### 9.3 鏂扮殑閫愯缁撹锛堜笉鍗囩骇涓烘寮?C04锛?
| 琛?| 缁撹 | 璇佹嵁 |
|---|---|---|
| G3-01 | **PASS锛圥layer 棣栨 mark锛?* | V2 Player `SubmitTileMark APPLIED`銆?19銆佲垝1銆佺鏈?group锛?36 涓嶅彉 |
| G3-02 | **PASS锛圥layer immediate + SECOND锛?* | V2 Player `SubmitSleeveBloom APPLIED`銆?09銆乧hoice APPLIED銆乨rawn 鍙?second銆佺墿鐞?hash 鍙樺寲 |
| G3-03 | **PASS锛圥layer BeamSwap + SWAP锛岀嫭绔?run锛?* | V1 淇鍚?run `SubmitBeamSwap`/`SubmitBeamSwapChoice` 鍧?APPLIED銆?10銆?36锛涙湭涓?G3-01/02 鍚?run 杩炵画 |
| G3-04 | **PARTIAL** | 涓?run 鍒嗗埆绮剧‘楠岃瘉 鈭?/119銆佲垝10/109銆佲垝10/110锛涙湭鍙栧緱鍗?run 涓夌瑪 119/109/99 杩炵画璇佹嵁 |
| G3-05 | **PASS锛堝垎 run锛?* | 涓?run 鐨?authority before/after 鍧?136/136锛涙湭鍦ㄥ悓涓€ run 瀹屾垚涓ゆ swap 鍚庣粓鎬?|
| G3-06 | **.NET PASS / PLAYER NOT_RUN** | .NET 涓ょ嫭绔?request Rejected 闆跺閲忥紱Player negative 鏈繍琛?|

### 9.4 R1 浠嶅紑鏀鹃」

- **R1-04 MAJOR**锛歅layer 璇佹嵁 verifier 浠嶉渶鐪熸闂ㄧ `pass=true/status=PASS/瀹屾暣鍏/鍞竴 request/瀹為檯瑙傚療/璐圭敤/136`锛涙湰杞湭瀹屾垚璇?verifier 寮哄寲锛屾棫 `MISSING_RAW_HASH` 鍋囩豢闈粛瀛樺湪銆?- **R1-05 MAJOR**锛氬皻鏈湪**鍚屼竴 Player run** 瀹屾垚涓夊姩浣滀笌涓ゆ鐪熷疄 swap锛汼leeveBloom 鍚庣浜屾 discard 瓒呮椂闇€瀹氫綅锛堟湰杞湭鏀?Actor/Domain/Client锛屼笉鐚滄牴鍥狅級銆?- **R1-06 MAJOR**锛歅layer build 浠?`errors=3`锛況unner 浠嶉潪 fresh-staging/瓒呮椂/ownership 鍏ㄩ棴鍚堛€傛湰杞彧閲嶈窇鍙楁祴闂寘锛屾湭澹扮О闂ㄧ宸蹭慨銆?
缁撹涓婇檺涓嶅彉锛氭湰鍙楁祴 D04 鍊欓€夌殑涓撻」璇佹嵁锛屼笉鏄寮?C04/G2 閫氳繃锛汸R20 浠?OPEN/Draft銆?
## 10. R2 返修（2026-11-01，追加）

本轮只关闭可执行的 R1-04/R1-06 机制面；R1-05 未伪装通过。

### 10.1 R1-04 严格证据门禁（部分关闭）

- 新增 `Assert-C04EvidenceStrict`：要求 `status=PASS`、所有 assertion `pass=true`、`blockedRows` 为空、完整 G3-01/02/03 成功断言（含两个 choice）、至少 5 条唯一 APPLIED request、每条 request 的 round/decision/seat=0、canonical 与 raw 均 64-hex；negative 要求对应 Rejected/zero-delta 断言与至少一条 REJECTED。
- runner 改用 strict；工具 fixture 从 11 项扩到 **19 项**，新增 strict valid fixture 与 7 个无效变体（status FAIL、全 false、Rejected 当成功、全 blocked、foreign identity、坏 raw hash、重复 request），全部按预期拒绝，native 0。
- **仍未关闭**：没有解析 canonical 原 bytes/scope digest、没有把 accepted 私有观察与 authority/save/outbox 原件交叉关联；proof 仍依赖 probe 自报 assertion/request 字段，不能达到 caller 要求的完整语义门禁。

### 10.2 R1-06 runner/build 机制（部分关闭）

- PlayerPath 存在性检查改为仅 `-SkipBuild` 时要求；非 SkipBuild 允许 fresh 不存在输出。
- EvidenceRoot 必须位于本树 `build/` 下；manifest 不再排除 `summary.txt`，summary 先写后封存。
- 仍未实现：独立 pinned staging、skip-build 不可变 source/build/fullpackage 绑定、Start-Process 有界 timeout/owned PID start-time、3 个 BuildReport errors 的逐 step 归因、失败阶段 seal。原 player-build2 `errors=3` 保留，未称 0 error。

### 10.3 R1-05 完整执行（未关闭）

同一 Player run 仍未完成三动作+两次 swap；runB 的 `G3-ADVANCE-2` 第二次 discard 仍失败。`DiscardEntity` 仍只观察 StateVersion 增长，未记录点击门控/pending/原 request/receipt/publication；未定位根因，未延长 sleep 或关闭门控。PlayMode 未重跑，negative 仍 NOT_RUN。两 run 原件继续保留，不合并为六行 PASS。

结论上限不变：本受测 D04 候选专项证据，不是正式 C04/G2 通过。
