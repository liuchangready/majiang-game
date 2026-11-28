# M1-05 配对批跑联调与证据边界

本文件记录 M1-05 真实四席配对批跑在 `ai_dev`（含 M1-04 四席 `CharacterAiProfile` 与 `ScoredCheatBasicAiStrategy`）上的接入合同与证据边界。骨架原型来自 `f753cd4a00fc5b84230f5fde7b74885b752efdb2`，经联调接入 `LocalTableHost` 公共入口并保留全部既有统计与负例保护。

## CLI 模式

1. **单策略模式（兼容保留）**：
   `CheatAiBatchRunner --rounds N [--seed N] [--output DIR] [--round-timeout-seconds N] [--overall-timeout-minutes N]`
   或四席显式配置：`--rounds N --seat-profiles P0,P1,P2,P3 ...`
2. **仅生成计划（兼容保留）**：
   `CheatAiBatchRunner --plan-pairs N --seed S --candidate-profile A --control-profile B --output DIR`
   仍写出未执行的确定性配对计划（`not-run`，退出码 `3`），用于计划核对与离线格式验证。
3. **正式配对批跑执行模式**：
   `CheatAiBatchRunner --run-pairs N --seed S --candidate-profile A --control-profile B [--opponent-profile P] [--character-ids C0,C1,C2,C3] [--ruleset-version V] [--max-spend-per-round M] [--reserve-points R] [--round-timeout-seconds T] [--overall-timeout-minutes M] --output DIR`
   - 对每个配对组 `pair-0001 .. pair-N` 依次执行目标席 `0, 1, 2, 3` 轮换；每个轮换包含 `candidate` 与 `control` 两条腿，其余三席固定 `--opponent-profile`、四席规则角色固定 `--character-ids`、规则版本固定 `--ruleset-version`。
   - **随机流隔离**：同一 `(pairIndex, rotationIndex)` 的两条腿共用同一个 SplitMix64 派生的洗牌环境种子（`PairedRunPlan.DeriveShuffleSeed(rotationSeed)` -> `LocalHostEnvironment`）；四个席位的策略随机流按 `(rotationSeed, seat)` 独立派生（`PairedRunPlan.DeriveSeatStrategySeed(rotationSeed, seat)`），与洗牌流完全隔离。
4. **统计与不变量自检（兼容保留并扩展）**：
   `CheatAiBatchRunner --self-test-paired-statistics`

## 证据来源、关联与公私投影隔离

- **真实权威输入**：逐局通过 `LocalTableHost.OpenRoomAsync` 与 `LocalTableHost.GetRoundSummaryAsync` 获取终局状态、四席终分、`RemainingRiichiSticks`、136 个物理实体 ID 守恒报告（`WallAssignedCount == 136`, `WallConserved == true`）以及权威账本（`CheatBalances`、`SeatCheatUsage`）。
- **机会与回执关联**：`LocalTableDriver` 在捕获权威窗口（`CaptureAiCheatOpportunityAsync` / `CaptureAiObservationAsync`）与提交/Tick 裁决后通过 `ILocalAiDecisionObserver` 发出脱敏 `LocalAiDecisionRecord`，关联 `RoundId`、`Seat`、`Phase`、`StateVersion`、`ControlEpoch`、`RecoveryGeneration`、`DecisionId`、`RequestId`、`OfferId`、`WindowId`、`IsOfferChoice`、`HasThirdCandidate`、`LegalOptionCount`、`LegalCapabilities`、`OptionCost`、`AuthoritativeStateVersionAfter`、`EventSeq`、`ReceiptStatus`、`Replayed` 与 `IsDefaultResolution`。
- **跳过与默认裁决不反推**：可选千术窗口策略选择跳过时发出 `cheat-skip`（带真实 `LegalOptionCount` 与 `LegalCapabilities`），权威超时默认结算发出 `timeout-default`，不从选中动作或终态反推机会数。
- **公私投影隔离**：`decisions.jsonl`、`paired-rounds.jsonl`、`paired-run.json` 严禁写入暗牌（未公开手牌、牌山顺序、王牌、未标记换牌槽）或随机种子。
- **分母与首败口径**：仅 8 腿均完整且通过终态、136 牌守恒、100,000 点守恒与账本对账的配对组进入 `CompletePairs` 与 95% Student-t 区间分母；失败、超时与未运行组绝不以零分替补或计入完整组，并按配对序、轮换序、候选先于对照保留首败。
- **独占输出与封存**：输出目标必须为不存在的独占目录（同级排他锁 + staging 原子目录发布）；落盘后生成 `sha256.txt` 清单。
