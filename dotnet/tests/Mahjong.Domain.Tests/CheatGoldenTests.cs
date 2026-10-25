using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// V5-1 P1.4 golden 重放：<c>tests/v5-golden/cheat-v1.json</c> 是唯一 oracle。
    ///
    /// 该文件由只读 V4 导出器 <c>engine/src/test/kotlin/com/majiang/v3/V5CheatGoldenExporterTest.kt</c>
    /// 在真实 V4 生产源（economy.CheatLedger / cheat.mark.TileMarkState / authority.cheat 三个
    /// 权威预检处理器 / model.TileMovement）上运行导出，文件头记录 v4Commit 与导出命令。
    /// 本测试的一切期望值都先从文件读出再重放到 C# 域层：严禁手填预期值，
    /// 也不允许把 C# 实现本身当作黄金来源。
    ///
    /// 槽位映射（由本测试持有，不进入导出器）：
    /// V4 活牌山列表下标 wall[k] ↔ C# 活墙区绝对槽 Wall(k + 53)（标准发牌消耗区下标
    /// 0..52，摸牌指针从 53 起）；V4 deadWall[i] ↔ DeadWall(i)、replacement[i] ↔
    /// DeadWallReplacement(i)（王牌区两侧行无偏移）。手牌只按“座位 + 实体集合”对照。
    /// </summary>
    public class CheatGoldenTests
    {
        private const string Schema = "v5-cheat-golden-v1";
        private const int WallSlotOffset = 53;
        private const int Seat = 1;

        private static readonly TileId T0 = new TileId(0);
        private static readonly TileId T1 = new TileId(1);
        private static readonly TileId T2 = new TileId(2);
        private static readonly TileId T3 = new TileId(3);
        private static readonly TileId T4 = new TileId(4);

        // ------------------------------------------------------------------
        // V4 D-09 拒绝原因串 → C# CheatErrorCode 映射契约。
        // 值为映射完备性声明；带重放的条目同时被下方代表案例用真实 C# 输出验证。
        // V4 与 C# 的形态差异（异常 vs 类型化结果、更细的特化码）在注释中标明。
        // ------------------------------------------------------------------
        private static readonly Dictionary<string, CheatErrorCode> ReasonMap = new()
        {
            ["NOT_YOUR_TURN"] = CheatErrorCode.NotYourTurn,
            ["WRONG_PHASE"] = CheatErrorCode.WrongPhase, // C# 把 response-window 特化为 ResponseWindowOpen（同族）
            ["DECISION_MISMATCH"] = CheatErrorCode.InvalidRequest, // V4 决策绑定；C# 域层归并到 InvalidRequest
            ["NO_ACTIVE_DECISION"] = CheatErrorCode.InvalidRequest, // V4 timing 编排概念，C# 域层无对应输入
            ["DEADLINE_EXPIRED"] = CheatErrorCode.DeadlineExpired,
            ["INSUFFICIENT_FUNDS"] = CheatErrorCode.InsufficientFunds,
            ["SLEEVE_BLOOM_DISABLED"] = CheatErrorCode.Disabled,
            ["BEAM_SWAP_DISABLED"] = CheatErrorCode.Disabled,
            ["SLEEVE_BLOOM_NO_DRAWN_TILE"] = CheatErrorCode.NoDrawnTile,
            ["BEAM_SWAP_NO_DRAWN_TILE"] = CheatErrorCode.NoDrawnTile,
            ["SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED"] = CheatErrorCode.DealExtraDraw,
            ["BEAM_SWAP_DEALER_EXTRA_EXCLUDED"] = CheatErrorCode.DealExtraDraw,
            ["SLEEVE_BLOOM_ALREADY_PENDING"] = CheatErrorCode.OfferAlreadyPending,
            // V4 对“自己的 beam 已挂起”与“sleeve 挂起时激活 beam”共用一个 reason；
            // C# 按语境区分 OfferAlreadyPending / OtherOfferPending，主码取重复激活语义。
            ["BEAM_SWAP_ALREADY_PENDING"] = CheatErrorCode.OfferAlreadyPending,
            ["SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW"] = CheatErrorCode.AlreadyUsedThisTurn,
            ["BEAM_SWAP_ALREADY_USED_THIS_DRAW"] = CheatErrorCode.AlreadyUsedThisTurn,
            ["SLEEVE_BLOOM_WALL_EMPTY"] = CheatErrorCode.WallEmpty,
            ["BEAM_SWAP_NO_CANDIDATES"] = CheatErrorCode.NoWallCandidate,
            ["SLEEVE_BLOOM_NO_WINDOW"] = CheatErrorCode.NoWindow,
            ["SLEEVE_BLOOM_RESERVED_PENDING"] = CheatErrorCode.ReservedPending,
            ["SLEEVE_BLOOM_NO_PENDING_OFFER"] = CheatErrorCode.NoPendingOffer,
            ["SLEEVE_BLOOM_OFFER_MISMATCH"] = CheatErrorCode.OfferMismatch,
            ["SLEEVE_BLOOM_STALE_OFFER"] = CheatErrorCode.StaleOffer,
            ["BEAM_SWAP_OFFER_MISMATCH"] = CheatErrorCode.OfferMismatch,
            ["BEAM_SWAP_STALE_OFFER"] = CheatErrorCode.StaleOffer,
            // V4 把窗外槽与无效手牌都归 INVALID_CHOICE；C# 把窗外槽特化为 CandidateNotFound。
            ["BEAM_SWAP_INVALID_CHOICE"] = CheatErrorCode.InvalidSelection,
            ["BEAM_SWAP_RIICHI_LOCKED"] = CheatErrorCode.RiichiLocked,
            ["TILE_MARK_RIICHI_LOCKED"] = CheatErrorCode.RiichiLocked,
            ["TILE_MARK_INVALID_SELECTION"] = CheatErrorCode.InvalidSelection,
            ["TILE_MARK_TILE_NOT_OWNED"] = CheatErrorCode.TileNotOwned,
            ["TILE_MARK_ALREADY_MARKED"] = CheatErrorCode.AlreadyMarked,
            ["TILE_MARK_CAPACITY_EXCEEDED"] = CheatErrorCode.MarkCapacityExceeded,
            ["SLEEVE_BLOOM_CHOICE_PENDING"] = CheatErrorCode.OtherOfferPending,
            ["BEAM_SWAP_CHOICE_PENDING"] = CheatErrorCode.OtherOfferPending,
        };

        // ------------------------------------------------------------------
        // golden 装载与小工具
        // ------------------------------------------------------------------

        private static string RepoRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static JsonElement Golden()
        {
            string? overridePath = Environment.GetEnvironmentVariable("V5_CHEAT_GOLDEN_PATH");
            string path = string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(RepoRoot(), "tests", "v5-golden", "cheat-v1.json")
                : Path.GetFullPath(overridePath);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.Clone();
        }

        private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

        private static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

        private static int[] Ints(JsonElement e, string name) =>
            e.GetProperty(name).EnumerateArray().Select(x => x.GetInt32()).ToArray();

        private static JsonElement[] Cases(JsonElement root, string section) =>
            root.GetProperty(section).EnumerateArray().ToArray();

        private static JsonElement Case(JsonElement root, string section, string caseId) =>
            Cases(root, section).Single(c => Str(c, "caseId") == caseId);

        private static JsonElement Fact(JsonElement movementCase, string arrayName, int tileId) =>
            movementCase.GetProperty(arrayName).EnumerateArray()
                .Single(f => f.GetProperty("tileId").GetInt32() == tileId);

        private static string FactContainer(JsonElement movementCase, string arrayName, int tileId) =>
            Str(Fact(movementCase, arrayName, tileId), "container");

        private static bool Bool(JsonElement e, string name) => e.GetProperty(name).GetBoolean();

        // ------------------------------------------------------------------
        // 文件头：必须是真实导出，不是占位
        // ------------------------------------------------------------------

        [Fact]
        public void Golden_Header_IsAGenuineExport()
        {
            JsonElement root = Golden();

            Assert.Equal(Schema, Str(root, "schema"));
            Assert.Equal("exported", Str(root, "extractionStatus"));

            string commit = Str(root, "v4Commit");
            Assert.Equal(40, commit.Length);
            Assert.True(commit.All(Uri.IsHexDigit), "v4Commit must be a full git sha: " + commit);

            Assert.True(Int(root, "generatorVersion") >= 1);
            Assert.Equal(1, Int(root, "rulesetVersion"));
            Assert.Contains("V5_CHEAT_GOLDEN_EXPORT", Str(root, "generatedByCommand"));
            Assert.Contains("no expected value is hand-written", Str(root, "exportedAtNote"));

            // 六节齐全
            Assert.Equal(4, root.GetProperty("ledger").GetProperty("initialPoints").GetArrayLength());
            Assert.True(root.TryGetProperty("tileMarkState", out _));
            Assert.NotEmpty(Cases(root, "tileMarkHandler"));
            Assert.NotEmpty(Cases(root, "sleeveBloomHandler"));
            Assert.NotEmpty(Cases(root, "beamSwapHandler"));
            Assert.NotEmpty(Cases(root, "tileMovement"));
        }

        // ------------------------------------------------------------------
        // 账本节：逐值重放（V4 economy.CheatLedger → C# CheatLedger）
        // ------------------------------------------------------------------

        [Fact]
        public void Ledger_Section_ReplaysAgainstCheatLedger()
        {
            JsonElement root = Golden();
            JsonElement ledger = root.GetProperty("ledger");

            int[] initialPoints = Ints(ledger, "initialPoints");
            JsonElement charge = ledger.GetProperty("chargeOnce");

            // 单次入账
            CheatLedger ledgerCs = CheatLedger.Initialize(CheatTestBench.Economy(initialPoints[0]));
            CheatLedgerApplyResult charged = ledgerCs.Apply(
                Str(charge, "entryId"), Int(charge, "seat"), Int(charge, "amount"), Str(charge, "reason"));
            Assert.True(charged.Success, charged.Error.ToString());
            Assert.False(charged.Replayed);
            Assert.Equal(Int(charge, "balanceAfter"), charged.Entry!.BalanceAfter);
            int[] balancesAfter = Ints(charge, "balancesAfter");
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(balancesAfter[seat], charged.Ledger!.BalanceOf(seat));
            }

            Assert.Equal(Int(charge, "entryCount"), charged.Ledger!.Entries.Count);

            // 同 id 同载荷重放：返回原 entry 值，不重复入账
            JsonElement replay = ledger.GetProperty("replaySamePayload");
            CheatLedgerApplyResult replayed = charged.Ledger.Apply(
                Str(charge, "entryId"), Int(charge, "seat"), Int(charge, "amount"), Str(charge, "reason"));
            Assert.Equal(Bool(replay, "returnsOriginalEntry"), replayed.Replayed);
            Assert.Equal(Int(replay, "balanceAfter"), replayed.Entry!.BalanceAfter);
            Assert.Equal(Int(replay, "entryCount"), replayed.Ledger!.Entries.Count);

            // 同 id 异载荷：V4 抛 IllegalArgumentException 且零副作用；C# 返回类型化冲突且零副作用
            JsonElement conflict = ledger.GetProperty("conflictDifferentPayload");
            CheatLedgerApplyResult conflicted = replayed.Ledger.Apply(
                Str(charge, "entryId"), Int(conflict.GetProperty("requested"), "seat"),
                Int(conflict.GetProperty("requested"), "amount"), Str(charge, "reason"));
            Assert.False(conflicted.Success);
            Assert.Equal(CheatLedgerError.EntryConflict, conflicted.Error);
            Assert.Null(conflicted.Ledger);
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(Ints(conflict, "balancesAfter")[seat], replayed.Ledger.BalanceOf(seat));
            }
            Assert.Equal(Int(conflict, "entryCount"), replayed.Ledger.Entries.Count);

            // 透支 / 溢出 / 座位越界：V4 抛异常，C# 返回类型化错误；两者都必须零副作用
            JsonElement poorSpec = ledger.GetProperty("insufficientFunds");
            CheatLedger poor = CheatLedger.Initialize(CheatTestBench.Economy(Int(poorSpec, "initialPoints")));
            CheatLedgerApplyResult overdraft = poor.Apply("over", 1, Int(poorSpec, "amount"), "TILE_MARK");
            Assert.False(overdraft.Success);
            Assert.Equal(CheatLedgerError.InsufficientFunds, overdraft.Error);

            JsonElement overflowSpec = ledger.GetProperty("overflow");
            CheatLedgerApplyResult overflow = poor.Apply("big", 2, Int(overflowSpec, "amount"), "TILE_MARK");
            Assert.False(overflow.Success);
            Assert.Equal(CheatLedgerError.BalanceOverflow, overflow.Error);

            JsonElement seatSpec = ledger.GetProperty("invalidSeat");
            CheatLedgerApplyResult badSeat = poor.Apply("seat", Int(seatSpec, "seat"), -1, "TILE_MARK");
            Assert.False(badSeat.Success);
            Assert.Equal(CheatLedgerError.InvalidSeat, badSeat.Error);

            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(Int(poorSpec, "initialPoints"), poor.BalanceOf(seat));
            }
            Assert.Empty(poor.Entries);

            // 流水汇总：C# summary 是全局口径，逐座值从 entries 重算后对照黄金
            JsonElement flowSpec = ledger.GetProperty("flowSummary");
            CheatLedger flow = CheatLedger.Initialize(CheatTestBench.Economy(Int(poorSpec, "initialPoints")));
            flow = flow.Apply("spend", Int(flowSpec.GetProperty("spend"), "seat"), Int(flowSpec.GetProperty("spend"), "amount"), "TILE_MARK").Ledger!;
            flow = flow.Apply("gain", Int(flowSpec.GetProperty("gain"), "seat"), Int(flowSpec.GetProperty("gain"), "amount"), "ROUND_ACHIEVEMENT").Ledger!;

            int seatOfSpend = Int(flowSpec.GetProperty("spend"), "seat");
            int seatOfGain = Int(flowSpec.GetProperty("gain"), "seat");
            int spent = flow.Entries.Where(e => e.Seat == seatOfSpend && e.Amount < 0).Sum(e => -e.Amount);
            int gained = flow.Entries.Where(e => e.Seat == seatOfGain && e.Amount > 0).Sum(e => e.Amount);
            int net = flow.Entries.Where(e => e.Seat == seatOfGain).Sum(e => e.Amount);
            Assert.Equal(Int(flowSpec, "seat0TotalSpent"), spent);
            Assert.Equal(Int(flowSpec, "seat1TotalGained"), gained);
            Assert.Equal(Int(flowSpec, "seat1NetDelta"), net);

            // C# Summarize 是全局口径：TotalGained/TotalSpent 应等于逐座值之和
            CheatLedgerSummary summary = flow.Summarize();
            Assert.Equal(gained, summary.TotalGained);
            Assert.Equal(spent, summary.TotalSpent);
            Assert.Equal(gained - spent, summary.Net);

            int[] finalBalances = Ints(flowSpec, "finalBalances");
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(finalBalances[seat], flow.BalanceOf(seat));
            }

            // 快照往返：从 initialPoints 重放 entries 必须还原终局余额
            JsonElement snapshotSpec = ledger.GetProperty("snapshotRoundTrip");
            Assert.True(Bool(snapshotSpec, "balancesEqual"));
            Assert.True(Bool(snapshotSpec, "entriesEqual"));
            CheatLedger restored = CheatLedger.FromSnapshot(Int(poorSpec, "initialPoints"), finalBalances, flow.Entries);
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(finalBalances[seat], restored.BalanceOf(seat));
            }

            // The boolean is only the exporter's summary; bind every restored entry field
            // and its order as well (full matrix in CheatGoldenBindingTests).
            Assert.Equal(flow.Entries.Count, restored.Entries.Count);
            Assert.Equal(flow.Entries, restored.Entries);
            for (int i = 0; i < restored.Entries.Count; i++)
            {
                Assert.Equal(flow.Entries[i].EntryId, restored.Entries[i].EntryId);
                Assert.Equal(flow.Entries[i].Seat, restored.Entries[i].Seat);
                Assert.Equal(flow.Entries[i].Amount, restored.Entries[i].Amount);
                Assert.Equal(flow.Entries[i].BalanceAfter, restored.Entries[i].BalanceAfter);
                Assert.Equal(flow.Entries[i].Reason, restored.Entries[i].Reason);
            }
        }

        // ------------------------------------------------------------------
        // 标记状态节：逐值重放（V4 cheat.mark.TileMarkState → C# TileMarkState）
        // ------------------------------------------------------------------

        [Fact]
        public void TileMarkState_Section_ReplaysAgainstTileMarkState()
        {
            JsonElement spec = Golden().GetProperty("tileMarkState");

            // 首个发牌局次与同 id 重放
            JsonElement register = spec.GetProperty("registerDealtRound");
            TileMarkTransition registered = new TileMarkState().RegisterDealtRound("r1");
            Assert.True(registered.Success);
            Assert.Equal(Int(register, "ordinalAfterFirst"), registered.State!.DealtRoundOrdinal);
            Assert.Equal(Bool(register, "sameIdReplays"),
                ReferenceEquals(registered.State, registered.State.RegisterDealtRound("r1").State));

            // 加组：到期局次、版本代次、同 id 法律、跨座位
            JsonElement addSpec = spec.GetProperty("addGroup");
            int[] versionTrail = Ints(addSpec, "versionTrail");
            Assert.Equal(versionTrail[0], new TileMarkState().Version);
            Assert.Equal(versionTrail[1], registered.State!.Version);
            TileMarkGroup g1 = new TileMarkGroup("g1", 0, new[] { T0, T1, T2, T3 }, "r1", 1);
            TileMarkTransition added = registered.State.AddGroup(g1);
            Assert.True(added.Success);
            Assert.Equal(Int(addSpec, "expiresAfterRoundOrdinal"), added.State!.Groups[0].ExpiresAfterRoundOrdinal);
            Assert.Equal(versionTrail[2], added.State.Version);
            Assert.Equal(versionTrail[3], added.State.CompleteRound("r1").State!.Version);
            Assert.Equal(Int(spec.GetProperty("expiry"), "versionAfterFirstAdd"), added.State.Version);

            Assert.Equal(Bool(addSpec, "sameIdSameContentReplays"),
                ReferenceEquals(added.State, added.State.AddGroup(
                    new TileMarkGroup("g1", 0, new[] { T0, T1, T2, T3 }, "r1", 1)).State));
            Assert.False(added.State.AddGroup(
                new TileMarkGroup("g1", 0, new[] { T0, T1, T2, T4 }, "r1", 1)).Success);

            // 跨座位同实体允许
            Assert.Equal(Bool(addSpec, "crossSeatSameTilesAllowed"), added.State
                .AddGroup(new TileMarkGroup("g1b", 1, new[] { T0, T1, T2, T3 }, "r1", 1)).Success);
            TileMarkState crossSeat = added.State
                .AddGroup(new TileMarkGroup("g1b", 1, new[] { T0, T1, T2, T3 }, "r1", 1)).State!;
            Assert.Equal(Int(addSpec, "crossSeatGroupCount"), crossSeat.Groups.Count);

            // 到期序列：g1 于 ordinal 1 创建 → expiry 4；完成 r3 仍在，完成 r4 移除
            TileMarkState rolling = added.State;
            foreach (string roundId in new[] { "r1", "r2", "r3" })
            {
                rolling = rolling.CompleteRound(roundId).State!;
                if (roundId != "r3")
                {
                    rolling = rolling.RegisterDealtRound(NextRound(roundId)).State!;
                }
            }

            Assert.Equal(Int(spec.GetProperty("expiry"), "groupsAfterCompletingRound3"), rolling.Groups.Count);
            Assert.Equal(Int(spec.GetProperty("expiry"), "versionAfterCompletingRound3"), rolling.Version);
            TileMarkState final = rolling.RegisterDealtRound("r4").State!.CompleteRound("r4").State!;
            Assert.Equal(Int(spec.GetProperty("expiry"), "groupsAfterCompletingRound4"), final.Groups.Count);
            Assert.Equal(Int(spec.GetProperty("expiry"), "versionAfterCompletingRound4"), final.Version);
            Assert.Equal(Int(spec.GetProperty("expiry"), "settledRoundCount"), final.SettledRoundIds.Count);
        }

        private static string NextRound(string roundId) => "r" + (int.Parse(roundId.Substring(1)) + 1);

        // ------------------------------------------------------------------
        // 处理器节 1：文件中每个拒绝原因必须映射到 C# 错误码（完备性）
        // ------------------------------------------------------------------

        [Fact]
        public void Handler_Sections_CoverEveryRejectionReason()
        {
            JsonElement root = Golden();
            Assert.Equal(20, Cases(root, "tileMarkHandler").Length);
            Assert.Equal(32, Cases(root, "sleeveBloomHandler").Length);
            Assert.Equal(23, Cases(root, "beamSwapHandler").Length);
            var unmapped = new List<string>();
            int allowed = 0, rejected = 0, stale = 0;

            foreach (string section in new[] { "tileMarkHandler", "sleeveBloomHandler", "beamSwapHandler" })
            {
                foreach (JsonElement c in Cases(root, section))
                {
                    Assert.False(Str(c, "caseId").Length == 0);
                    Assert.False(Str(c, "note").Length == 0);
                    switch (Str(c, "outcome"))
                    {
                        case "Allowed":
                            allowed++;
                            break;
                        case "Rejected":
                        case "StaleOffer":
                            string reason = Str(c, "reason");
                            if (!ReasonMap.ContainsKey(reason))
                            {
                                unmapped.Add(section + "/" + Str(c, "caseId") + " -> " + reason);
                            }

                            if (Str(c, "outcome") == "Rejected")
                            {
                                rejected++;
                            }
                            else
                            {
                                stale++;
                            }

                            break;
                        default:
                            Assert.Fail("unknown outcome in " + section + ": " + Str(c, "outcome"));
                            break;
                    }
                }
            }

            Assert.True(unmapped.Count == 0, "unmapped V4 reasons: " + string.Join("; ", unmapped));
            Assert.True(allowed > 0 && rejected > 0 && stale > 0);
        }

        // ------------------------------------------------------------------
        // 处理器节 2：代表案例重放——C# 可构造的拒绝与放行，按黄金 reason 对码
        // ------------------------------------------------------------------

        [Fact]
        public void Handler_TileMark_RepresentativeCases_ReplayAgainstCheatSubmission()
        {
            JsonElement root = Golden();
            TilePositionMap map = CheatTestBench.CraftedOpening();
            CheatState state = CheatTestBench.InitialState();
            CheatEconomyConfig economy = CheatTestBench.Economy();
            CheatTurnContext ctx = CheatTestBench.Context(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);
            TileId[] owned = map.TilesInZone(TileZone.Hand, 0).Take(4).ToArray();

            // tm-not-your-turn
            CheatTransition notYours = CheatSubmission.ApplyTileMark(
                state, map, ctx, economy, 1, "req-a", "g", owned);
            AssertGoldenRejection(root, "tileMarkHandler", "tm-not-your-turn", notYours, CheatErrorCode.NotYourTurn);

            // tm-riichi-locked
            CheatTurnContext riichi = CheatTestBench.Context(
                0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, riichi: new[] { true, false, false, false });
            AssertGoldenRejection(root, "tileMarkHandler", "tm-riichi-locked",
                CheatSubmission.ApplyTileMark(state, map, riichi, economy, 0, "req-b", "g", owned),
                CheatErrorCode.RiichiLocked);

            // tm-response-window：V4 归并 WRONG_PHASE，C# 特化为 ResponseWindowOpen（同族）
            JsonElement responseCase = Case(root, "tileMarkHandler", "tm-response-window");
            CheatTurnContext window = CheatTestBench.Context(
                0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, responseWindow: true);
            CheatTransition response = CheatSubmission.ApplyTileMark(state, map, window, economy, 0, "req-c", "g", owned);
            Assert.False(response.Success);
            Assert.Equal(CheatErrorCode.ResponseWindowOpen, response.Error);
            Assert.Equal("WRONG_PHASE", Str(responseCase, "reason"));

            // tm-invalid-selection-three
            CheatTransition three = CheatSubmission.ApplyTileMark(
                state, map, ctx, economy, 0, "req-d", "g", owned.Take(3).ToArray());
            AssertGoldenRejection(root, "tileMarkHandler", "tm-invalid-selection-three", three, CheatErrorCode.InvalidSelection);

            // tm-tile-not-owned
            TileId[] foreign = map.TilesInZone(TileZone.Hand, 1).Take(4).ToArray();
            AssertGoldenRejection(root, "tileMarkHandler", "tm-tile-not-owned",
                CheatSubmission.ApplyTileMark(state, map, ctx, economy, 0, "req-e", "g", foreign),
                CheatErrorCode.TileNotOwned);

            // tm-insufficient-funds
            AssertGoldenRejection(root, "tileMarkHandler", "tm-insufficient-funds",
                CheatSubmission.ApplyTileMark(
                    CheatTestBench.InitialState(5), map, ctx, economy, 0, "req-f", "g", owned),
                CheatErrorCode.InsufficientFunds);

            // tm-already-marked：先成功一组，再以新请求重发同实体
            CheatTransition first = CheatSubmission.ApplyTileMark(state, map, ctx, economy, 0, "req-1", "g1", owned);
            Assert.True(first.Success, first.Message);
            AssertGoldenRejection(root, "tileMarkHandler", "tm-already-marked",
                CheatSubmission.ApplyTileMark(first.State!, map, ctx, economy, 0, "req-2", "g2", owned),
                CheatErrorCode.AlreadyMarked);
        }

        [Fact]
        public void Handler_SleeveBloom_RepresentativeCases_ReplayAgainstCheatSubmission()
        {
            JsonElement root = Golden();
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(Seat, out _, out _);
            CheatState state = CheatTestBench.InitialState();
            CheatEconomyConfig economy = CheatTestBench.Economy();
            CheatTurnContext ctx = CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);

            // sb-activate-insufficient
            AssertGoldenRejection(root, "sleeveBloomHandler", "sb-activate-insufficient",
                CheatSubmission.ActivateSleeveBloom(
                    CheatTestBench.InitialState(5), map, ctx, economy, CheatTestBench.Sleeve(), Seat, "req-a"),
                CheatErrorCode.InsufficientFunds);

            // sb-activate-disabled：V4 的真实输入是 configuredCost=5（golden note「cost must
            // be exactly 10」），不是 featureEnabled=false。C# 用更细的 CostMustBeTen 拒绝，
            // 仍为零副作用；不得换输入去匹配 Disabled。
            var disabledCosts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { CheatEconomyConfig.TileMarkCostKey, 10 },
                { CheatEconomyConfig.SleeveBloomCostKey, 5 },
                { CheatEconomyConfig.BeamSwapCostKey, 10 }
            };
            var disabledEconomy = new CheatEconomyConfig(true, 40, disabledCosts);
            CheatTransition disabled = CheatSubmission.ActivateSleeveBloom(
                state, map, ctx, disabledEconomy, CheatTestBench.Sleeve(), Seat, "req-b");
            JsonElement disabledCase = Case(root, "sleeveBloomHandler", "sb-activate-disabled");
            Assert.Equal("Rejected", Str(disabledCase, "outcome"));
            Assert.Equal("SLEEVE_BLOOM_DISABLED", Str(disabledCase, "reason"));
            Assert.False(disabled.Success);
            Assert.Equal(CheatErrorCode.CostMustBeTen, disabled.Error);
            Assert.Null(disabled.State);
            Assert.Null(disabled.Map);

            // 激活成功后才能触发 stale / already-used 案例
            CheatState activated = CheatSubmission.ActivateSleeveBloom(
                state, map, ctx, economy, CheatTestBench.Sleeve(), Seat, "req-1").State!;
            string offerId = activated.SleeveBloom.ImmediateOffer!.OfferId;

            // sb-choose-stale-turn：offer 冻结于上一回合（V4 场景 turn 12 → 漂移回合）
            AssertGoldenStaleOffer(root, "sleeveBloomHandler", "sb-choose-stale-turn",
                CheatSubmission.ChooseSleeveBloom(
                    activated, map,
                    CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, turn: 2),
                    Seat, offerId, SleeveBloomKeep.First));

            // sb-choose-first：一致 offer 的正常选择
            CheatTransition chosen = CheatSubmission.ChooseSleeveBloom(activated, map, ctx, Seat, offerId, SleeveBloomKeep.First);
            Assert.True(chosen.Success, chosen.Message);
            Assert.Null(chosen.State!.SleeveBloom.ImmediateOffer);

            // sb-activate-already-used：同回合内绝技已消耗
            AssertGoldenRejection(root, "sleeveBloomHandler", "sb-activate-already-used",
                CheatSubmission.ActivateSleeveBloom(chosen.State, map, ctx, economy, CheatTestBench.Sleeve(), Seat, "req-2"),
                CheatErrorCode.AlreadyUsedThisTurn);

            // sbo-activate-no-window：window id 与已开窗口不匹配（回转激活须在他家回合发起）
            AssertGoldenRejection(root, "sleeveBloomHandler", "sbo-activate-no-window",
                CheatSubmission.ActivateOffTurnSleeveBloom(
                    state, map,
                    CheatTestBench.Context((Seat + 1) % 4, RoundPhase.Draw, SleeveBloomDrawKind.Normal),
                    economy, CheatTestBench.Sleeve(), Seat, "req-3", "bloom-window-9-9-9"),
                CheatErrorCode.NoWindow);
        }

        [Fact]
        public void Handler_BeamSwap_RepresentativeCases_ReplayAgainstCheatSubmission()
        {
            JsonElement root = Golden();
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(Seat, out _, out _);
            CheatState state = CheatTestBench.InitialState();
            CheatEconomyConfig economy = CheatTestBench.Economy();
            CheatTurnContext ctx = CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);

            // bm-activate-insufficient
            AssertGoldenRejection(root, "beamSwapHandler", "bm-activate-insufficient",
                CheatSubmission.ActivateBeamSwap(
                    CheatTestBench.InitialState(5), map, ctx, economy, CheatTestBench.Sleeve(), "req-a", Seat),
                CheatErrorCode.InsufficientFunds);

            // bm-activate-disabled：与袖里藏花相同，V4 真实输入是 configuredCost=5。
            var disabledCosts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { CheatEconomyConfig.TileMarkCostKey, 10 },
                { CheatEconomyConfig.SleeveBloomCostKey, 10 },
                { CheatEconomyConfig.BeamSwapCostKey, 5 }
            };
            var disabledEconomy = new CheatEconomyConfig(true, 40, disabledCosts);
            CheatTransition disabled = CheatSubmission.ActivateBeamSwap(
                state, map, ctx, disabledEconomy, CheatTestBench.Sleeve(), "req-b", Seat);
            JsonElement disabledCase = Case(root, "beamSwapHandler", "bm-activate-disabled");
            Assert.Equal("Rejected", Str(disabledCase, "outcome"));
            Assert.Equal("BEAM_SWAP_DISABLED", Str(disabledCase, "reason"));
            Assert.False(disabled.Success);
            Assert.Equal(CheatErrorCode.CostMustBeTen, disabled.Error);
            Assert.Null(disabled.State);
            Assert.Null(disabled.Map);

            CheatState activated = CheatSubmission.ActivateBeamSwap(
                state, map, ctx, economy, CheatTestBench.Sleeve(), "req-1", Seat).State!;
            BeamSwapOffer offer = activated.BeamSwap.Offer!;
            TileId closedTile = offer.EligibleHandTileIds[0];

            // bm-choose-stale-turn
            AssertGoldenStaleOffer(root, "beamSwapHandler", "bm-choose-stale-turn",
                CheatSubmission.ChooseBeamSwap(
                    activated, map,
                    CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, turn: 2),
                    Seat, offer.OfferId, BeamSwapAction.Swap, closedTile, BeamSwapSlot.Wall(54)));

            // bm-choose-invalid-foreign-hand：V4 的真实输入是 handTileId=52（当前摸牌），
            // 摸牌不属于冻结的暗手集合，而不是另一座的手牌。
            AssertGoldenRejection(root, "beamSwapHandler", "bm-choose-invalid-foreign-hand",
                CheatSubmission.ChooseBeamSwap(
                    activated, map, ctx, Seat, offer.OfferId, BeamSwapAction.Swap,
                    map.DrawnTile!.Value.Tile, BeamSwapSlot.Wall(54)),
                CheatErrorCode.InvalidSelection);

            // bm-choose-keep：放行且不动任何牌
            CheatTransition keep = CheatSubmission.ChooseBeamSwap(
                activated, map, ctx, Seat, offer.OfferId, BeamSwapAction.KeepOriginal, null, null);
            Assert.True(keep.Success, keep.Message);
            JsonElement keepCase = Case(root, "beamSwapHandler", "bm-choose-keep");
            Assert.Equal("Allowed", Str(keepCase, "outcome"));

            // bm-activate-already-used：同回合已消耗
            AssertGoldenRejection(root, "beamSwapHandler", "bm-activate-already-used",
                CheatSubmission.ActivateBeamSwap(keep.State!, map, ctx, economy, CheatTestBench.Sleeve(), "req-2", Seat),
                CheatErrorCode.AlreadyUsedThisTurn);
        }

        private static void AssertGoldenRejection(
            JsonElement root, string section, string caseId, CheatTransition actual, CheatErrorCode expected)
        {
            JsonElement c = Case(root, section, caseId);
            Assert.Equal("Rejected", Str(c, "outcome"));
            Assert.Equal(ReasonMap[Str(c, "reason")], expected); // 黄金 reason 必须映射到预期的 C# 码
            Assert.False(actual.Success, caseId + " should be rejected: " + actual.Message);
            Assert.Equal(expected, actual.Error);
            Assert.Null(actual.State);
            Assert.Null(actual.Map);
        }

        private static void AssertGoldenStaleOffer(JsonElement root, string section, string caseId, CheatTransition actual)
        {
            JsonElement c = Case(root, section, caseId);
            Assert.Equal("StaleOffer", Str(c, "outcome"));
            Assert.Equal(CheatErrorCode.StaleOffer, ReasonMap[Str(c, "reason")]);
            Assert.False(actual.Success, caseId + " should be stale: " + actual.Message);
            Assert.Equal(CheatErrorCode.StaleOffer, actual.Error);
            // stale 结果仍清结算：返回清掉对应 offer 的新状态、不动任何牌
            Assert.NotNull(actual.State);
            Assert.Null(actual.Map);
        }

        // ------------------------------------------------------------------
        // 移动节：物理事实（V4 model.TileMovement → C# 绝技入口）
        // ------------------------------------------------------------------

        [Fact]
        public void TileMovement_Section_CarriesTheExportedLawsInOrder()
        {
            JsonElement[] movement = Cases(Golden(), "tileMovement");
            string[] expected =
            {
                "mv-swap-hand-with-wall-direct",
                "mv-swap-drawn-with-wall-backfill",
                "mv-reservation-delivery",
                "mv-reservation-delivery-then-beam",
                "mv-rinshan-and-drawn-swap",
                "mv-swap-hand-with-dead-wall",
            };
            Assert.Equal(expected, movement.Select(c => Str(c, "caseId")).ToArray());
        }

        /// <summary>
        /// mv-swap-hand-with-wall-direct 的同构重放。V4 案例置换 wall[0]；C# 偷梁换柱
        /// 入口的冻结窗口从当前摸牌指针起，只能选指针之后的槽，这里用 Wall(54)
        /// （↔V4 wall[1]）重放同一法律：两实体容器互换、摸牌指针不动、136 张守恒。
        /// </summary>
        [Fact]
        public void TileMovement_SwapHandWithWall_ReplaysContainerExchange()
        {
            JsonElement m1a = Case(Golden(), "tileMovement", "mv-swap-hand-with-wall-direct");

            // 黄金结构：手牌实体进墙槽、墙实体进手、指针不动
            int handTileId = Int(m1a, "handTileId");
            int displacedTileId = Int(m1a, "displacedTileId");
            Assert.Equal("wall", FactContainer(m1a, "factsAfter", handTileId));
            Assert.Equal(Int(m1a, "wallIndex"), Int(Fact(m1a, "factsAfter", handTileId), "index"));
            Assert.Equal("hand", FactContainer(m1a, "factsAfter", displacedTileId));
            Assert.Equal(Int(m1a, "wallPointerBefore"), Int(m1a, "wallPointerAfter"));

            // C# 同构重放
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(Seat, out _, out _);
            CheatState activated = CheatSubmission.ActivateBeamSwap(
                CheatTestBench.InitialState(), map,
                CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal),
                CheatTestBench.Economy(), CheatTestBench.Sleeve(), "req-1", Seat).State!;
            TileId handTile = activated.BeamSwap.Offer!.EligibleHandTileIds[0];
            TilePosition handSlot = map.PositionOf(handTile);
            TileId wallTile = map.TileAt(TilePosition.Wall(WallSlotOffset + 1));

            CheatTransition result = CheatSubmission.ChooseBeamSwap(
                activated, map,
                CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal),
                Seat, activated.BeamSwap.Offer!.OfferId, BeamSwapAction.Swap, handTile, BeamSwapSlot.Wall(WallSlotOffset + 1));

            Assert.True(result.Success, result.Message);
            TilePositionMap moved = result.Map!;
            Assert.True(moved.PositionOf(handTile).Equals(TilePosition.Wall(WallSlotOffset + 1)));
            Assert.True(moved.PositionOf(wallTile).Equals(handSlot));
            Assert.Equal(map.WallPointer, moved.WallPointer);
            Assert.Equal(map.WallEndExclusive, moved.WallEndExclusive);
            CheatTestBench.AssertConserved(moved);
        }

        /// <summary>
        /// mv-swap-drawn-with-wall-backfill 并非 V4-only：ChooseSleeveBloom(SECOND) 的
        /// normal-draw 分支正是摸牌实体与活墙候选互换（SwapHandWithWall 允许 drawn 作为
        /// 手牌来源并重写 drawn evidence）。旧注释把入口误限为 ChooseBeamSwap（它按设计
        /// 拒绝摸牌实体），本轮改为真实入口重放；逐字段精确重建见
        /// CheatGoldenBindingTests.Movement_DrawnBackfill_ReplaysThroughSleeveBloomSecondChoice。
        /// </summary>
        [Fact]
        public void TileMovement_DrawnBackfill_ReplaysThroughSleeveBloomSecondChoice()
        {
            JsonElement m1b = Case(Golden(), "tileMovement", "mv-swap-drawn-with-wall-backfill");
            Assert.Equal("wall", FactContainer(m1b, "factsAfter", Int(m1b, "drawnTileId")));
            Assert.Equal("hand", FactContainer(m1b, "factsAfter", Int(m1b, "displacedTileId")));
            Assert.Equal(Int(m1b, "backfilledTileId"), Int(m1b, "displacedTileId"));
            Assert.Equal(Int(m1b, "resolvedIndex") - 1, Int(m1b, "backfilledIndex"));
            Assert.Equal(Int(m1b, "wallPointerBeforeDraw") + 1, Int(m1b, "wallPointerAfterDraw"));
            Assert.Equal(Int(m1b, "wallPointerAfterDraw"), Int(m1b, "wallPointerAfterSwap"));

            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(Seat, out TileId drawnTile, out TileId nextWallTile);
            CheatTurnContext ctx = CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);
            CheatTransition activated = CheatSubmission.ActivateSleeveBloom(
                CheatTestBench.InitialState(), map, ctx, CheatTestBench.Economy(),
                CheatTestBench.Sleeve(), Seat, "req-1");
            Assert.True(activated.Success, activated.Message);
            SleeveBloomOffer offer = activated.State!.SleeveBloom.ImmediateOffer!;
            Assert.Equal(drawnTile, offer.FirstTile);
            Assert.Equal(nextWallTile, offer.SecondTile);

            int candidateSlot = map.PositionOf(nextWallTile).Index;
            CheatTransition chosen = CheatSubmission.ChooseSleeveBloom(
                activated.State, map, ctx, Seat, offer.OfferId, SleeveBloomKeep.Second);
            Assert.True(chosen.Success, chosen.Message);
            TilePositionMap moved = chosen.Map!;
            Assert.Equal(TilePosition.Wall(candidateSlot), moved.PositionOf(drawnTile));
            Assert.Equal(TileZone.Hand, moved.PositionOf(nextWallTile).Zone);
            Assert.Equal(nextWallTile, moved.DrawnTile!.Value.Tile);
            Assert.Equal(map.WallPointer, moved.WallPointer);
            CheatTestBench.AssertConserved(moved);
        }

        /// <summary>
        /// mv-reservation-delivery：预留牌被所有者的下一次普通摸牌从活墙拉出。
        /// 黄金结构断言 + C# 交付入口重放。共享事实：预留牌进手、成为该座的普通摸牌
        /// 证据、预留与窗口清除、136 张守恒。C# 交付即 V4 removeAt 同构：预留槽后
        /// 实体前移一格、wallEndExclusive-1、指针仅在 index&lt;wallPointer 时回退——
        /// 黄金的「指针不动、墙尾缩一」在 C# 直接成立，无记账表示差异。
        /// </summary>
        [Fact]
        public void TileMovement_ReservationDelivery_ReplaysThroughCsharpEntrypoint()
        {
            JsonElement m2 = Case(Golden(), "tileMovement", "mv-reservation-delivery");

            // 黄金结构（V4 观测）：wallEndExclusive 缩一、指针不动、预留牌进手
            Assert.True(Bool(m2, "fromReservation"));
            Assert.Equal(Int(m2, "wallEndExclusiveBefore") - 1, Int(m2, "wallEndExclusiveAfter"));
            Assert.Equal(Int(m2, "wallPointerBefore"), Int(m2, "wallPointerAfter"));
            Assert.Equal("hand", FactContainer(m2, "factsAfter", Int(m2, "reservedTileId")));

            // C# 重放：交付以“座位下一次普通摸牌”为时点——先弃掉已持摸牌得到安静手
            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(Seat, out TileId drawnTile, out _);
            map = map.Discard(Seat, drawnTile).RequireState();
            TileId reservedTile = map.TileAt(TilePosition.Wall(WallSlotOffset + 1));
            CheatState baseState = CheatTestBench.InitialState();
            CheatState reserved = baseState.WithSleeveBloom(baseState.SleeveBloom.WithReservation(
                new SleeveBloomReservation(Seat, "bloom-offturn-1-x", reservedTile, null, 1)));

            CheatTransition delivered = CheatSubmission.DeliverSleeveBloomReservation(
                reserved, map,
                CheatTestBench.Context(Seat, RoundPhase.Draw, SleeveBloomDrawKind.Normal),
                Seat);

            Assert.True(delivered.Success, "reservation delivery failed: " + delivered.Message);
            TilePositionMap moved = delivered.Map!;
            Assert.True(moved.PositionOf(reservedTile).Zone == TileZone.Hand); // 共享事实：预留牌进手
            Assert.True(moved.PositionOf(reservedTile).Seat == Seat);
            Assert.True(moved.DrawnTile!.Value.Tile.Equals(reservedTile)); // 且成为该座普通摸牌证据
            Assert.Equal(DrawSource.Wall, moved.DrawnTile.Value.Source);
            CheatTestBench.AssertConserved(moved);
            Assert.Null(delivered.State!.SleeveBloom.ReservationFor(Seat)); // 预留与窗口随之清除
            CheatEvent deliveryEvent = Assert.Single(delivered.Events); // P3.1 边界：交付自有 owner-scoped 事件
            Assert.Equal(CheatEventKind.SleeveBloomReservationDelivered, deliveryEvent.Kind);
            Assert.Equal("bloom-offturn-1-x", deliveryEvent.OfferId);

            // removeAt 同构：与黄金同关系——指针不动、墙尾收缩一格
            Assert.Equal(map.WallPointer, moved.WallPointer);
            Assert.Equal(map.WallEndExclusive - 1, moved.WallEndExclusive);
        }

        /// <summary>
        /// sb-activate-allowed → sb-choose-second 成功路径对照：goldenInputs 是导出器从
        /// V4 校验器真实构造的 input 对象提取的输入事实。C# 夹具绑定其关系真值
        /// （第一候选=摸牌、第二候选=激活时活墙指针槽首牌、同回合选择、成本 10、
        /// 初始 40 点），行为断言验证 G-040b 语义：SECOND 精确互换、墙指针不前进、
        /// 余额 40-10=30、换出的牌就是下一手普通摸牌（后续抽牌序列等价）。
        /// </summary>
        [Fact]
        public void SleeveBloomSecondChoice_BindsToGoldenInputs()
        {
            JsonElement root = Golden();
            JsonElement actGolden = Case(root, "sleeveBloomHandler", "sb-activate-allowed").GetProperty("goldenInputs");
            JsonElement choiceGolden = Case(root, "sleeveBloomHandler", "sb-choose-second").GetProperty("goldenInputs");

            // 黄金输入关系：NORMAL、同回合选择、第一候选=摸牌、第二候选=第一可用墙槽
            Assert.Equal("NORMAL", Str(actGolden, "drawKind"));
            Assert.Equal("SECOND", Str(choiceGolden, "keep"));
            Assert.Equal(Int(choiceGolden, "offerCreatedTurnNumber"), Int(choiceGolden, "turnNumber"));
            Assert.Equal(Int(choiceGolden, "drawnTileId"), Int(choiceGolden, "offerFirstTileId"));
            Assert.Equal(Int(choiceGolden, "offerSecondWallIndex"), Int(choiceGolden, "nextAvailableWallIndex"));
            Assert.Equal(Int(actGolden, "nextWallTileId"), Int(choiceGolden, "offerSecondTileId"));

            TilePositionMap map = CheatTestBench.OpeningAfterNormalDraw(Seat, out TileId drawnTile, out _);
            CheatState activated = CheatSubmission.ActivateSleeveBloom(
                CheatTestBench.InitialState(), map,
                CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal),
                CheatTestBench.Economy(), CheatTestBench.Sleeve(), Seat, "req-1").State!;

            SleeveBloomOffer offer = activated.SleeveBloom.ImmediateOffer!;
            Assert.True(offer.FirstTile.Equals(drawnTile)); // 黄金：offerFirst == drawn
            Assert.Equal(Int(actGolden, "cheatPoints") - Int(actGolden, "cost"), activated.Ledger.BalanceOf(Seat));
            int candidateSlot = map.PositionOf(offer.SecondTile).Index;
            Assert.Equal(TileZone.Wall, map.PositionOf(offer.SecondTile).Zone);
            Assert.Equal(map.WallPointer, candidateSlot); // 黄金：第二候选=指针槽首牌（V4 53→C# 54）

            CheatTransition chosen = CheatSubmission.ChooseSleeveBloom(
                activated, map,
                CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal),
                Seat, offer.OfferId, SleeveBloomKeep.Second);
            Assert.True(chosen.Success, chosen.Message);
            TilePositionMap swapped = chosen.Map!;
            Assert.True(swapped.PositionOf(drawnTile).Equals(TilePosition.Wall(candidateSlot))); // 摸牌去候选槽
            Assert.Equal(TileZone.Hand, swapped.PositionOf(offer.SecondTile).Zone); // 候选进手
            Assert.Equal(map.WallPointer, swapped.WallPointer); // G-040b：墙指针不前进
            CheatTestBench.AssertConserved(swapped);
            Assert.Null(chosen.State!.SleeveBloom.ImmediateOffer);
            Assert.Equal(activated.Ledger, chosen.State.Ledger); // 选择不再扣费、不退款

            // 后续抽牌序列：换出的牌落在指针槽上，下一手普通摸牌摸到的正是它
            TilePositionMap nextDraw = swapped.DrawFromWall((Seat + 1) % 4).RequireState();
            Assert.Equal(TileZone.Hand, nextDraw.PositionOf(drawnTile).Zone);
        }

        /// <summary>
        /// sb-activate-rinshan-allowed → sb-choose-rinshan 成功路径对照：岭上第二候选=
        /// 下一张王牌（黄金 nextDeadWallTileId=offerSecond=28、rinshanPointer=1），
        /// SECOND 与 mv-rinshan-and-drawn-swap 同构：摸牌与下一张岭上牌原地对换。
        /// </summary>
        [Fact]
        public void SleeveBloomRinshanSecondChoice_BindsToGoldenInputs()
        {
            JsonElement root = Golden();
            JsonElement actGolden = Case(root, "sleeveBloomHandler", "sb-activate-rinshan-allowed").GetProperty("goldenInputs");
            JsonElement choiceGolden = Case(root, "sleeveBloomHandler", "sb-choose-rinshan").GetProperty("goldenInputs");

            Assert.Equal("RINSHAN", Str(actGolden, "drawKind"));
            Assert.Equal("SECOND", Str(choiceGolden, "keep"));
            Assert.Equal(Int(actGolden, "drawnTileId"), Int(choiceGolden, "offerFirstTileId"));
            Assert.Equal(Int(actGolden, "nextDeadWallTileId"), Int(choiceGolden, "offerSecondTileId"));

            TilePositionMap map = CheatTestBench.OpeningAfterRinshanDraw(Seat, out TileId drawnTile, out TileId nextRinshanTile);
            CheatState activated = CheatSubmission.ActivateSleeveBloom(
                CheatTestBench.InitialState(), map,
                CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan),
                CheatTestBench.Economy(), CheatTestBench.Sleeve(), Seat, "req-1").State!;

            SleeveBloomOffer offer = activated.SleeveBloom.ImmediateOffer!;
            Assert.True(offer.FirstTile.Equals(drawnTile));
            Assert.True(offer.SecondTile.Equals(nextRinshanTile)); // 黄金：岭上第二候选=下一张王牌
            Assert.True(map.TileAt(TilePosition.DeadWall(map.RinshanPointer)).Equals(offer.SecondTile));
            Assert.Equal(Int(actGolden, "cheatPoints") - Int(actGolden, "cost"), activated.Ledger.BalanceOf(Seat));

            CheatTransition chosen = CheatSubmission.ChooseSleeveBloom(
                activated, map,
                CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan),
                Seat, offer.OfferId, SleeveBloomKeep.Second);
            Assert.True(chosen.Success, chosen.Message);
            TilePositionMap swapped = chosen.Map!;
            // 黄金 m3 同构：摸牌与下一张岭上牌原地对换（摸牌占据下一张岭上槽）
            Assert.True(swapped.PositionOf(drawnTile).Equals(TilePosition.DeadWall(map.RinshanPointer)));
            Assert.Equal(TileZone.Hand, swapped.PositionOf(nextRinshanTile).Zone);
            CheatTestBench.AssertConserved(swapped);
            Assert.Equal(Int(actGolden, "cheatPoints") - Int(actGolden, "cost"), chosen.State!.Ledger.BalanceOf(Seat));
        }

        /// <summary>
        /// sbo-activate-allowed → sbo-choose-allowed → mv-reservation-delivery 全流程对照：
        /// 他家 DISCARD 回合激活（黄金 seat=2、current=0）、deadline 前选择 SECOND、
        /// 预留牌落在活墙（黄金 wallIndex=1 ≥ 指针 0 的同构关系）、所有者下一次普通
        /// 摸牌投递（黄金 factsAfter：预留牌进手）。C# 交付即 V4 removeAt 同构：
        /// 指针不动、墙尾缩一与黄金数值关系一致；后续抽牌序列（预留槽前首张照常被
        /// 下一席摸走、后继实体前移后紧随）直接复现 V4 行为。
        /// </summary>
        [Fact]
        public void OffTurnReservationPath_BindsToGoldenInputsAndDelivers()
        {
            JsonElement root = Golden();
            JsonElement actGolden = Case(root, "sleeveBloomHandler", "sbo-activate-allowed").GetProperty("goldenInputs");
            JsonElement choiceGolden = Case(root, "sleeveBloomHandler", "sbo-choose-allowed").GetProperty("goldenInputs");
            JsonElement m2 = Case(root, "tileMovement", "mv-reservation-delivery");

            // 黄金输入关系：他家 DISCARD 回合激活、deadline 前选择 SECOND、两候选都在活墙
            Assert.Equal(2, Int(actGolden, "seat"));
            Assert.Equal(0, Int(actGolden, "currentPlayer"));
            Assert.Equal("DISCARD", Str(actGolden, "phase"));
            Assert.Equal(30, Int(actGolden, "cheatPoints") - Int(actGolden, "cost"));
            Assert.Equal("SECOND", Str(choiceGolden, "keep"));
            Assert.True(choiceGolden.GetProperty("now").GetInt64() < choiceGolden.GetProperty("deadlineMillis").GetInt64());
            Assert.True(Bool(choiceGolden, "offerFirstInWall"));
            Assert.True(Bool(choiceGolden, "offerSecondInWall"));

            // 黄金 mv-reservation-delivery 结构（V4 观测）
            Assert.Equal(1, Int(m2, "wallIndex"));
            Assert.Equal(Int(m2, "wallEndExclusiveBefore") - 1, Int(m2, "wallEndExclusiveAfter"));
            Assert.Equal(Int(m2, "wallPointerBefore"), Int(m2, "wallPointerAfter"));

            // C# 全流程：窗口签发 → 他家（0）DISCARD 回合激活 → SECOND → 所有者投递
            CheatState state = CheatTestBench.InitialState().WithSleeveBloom(
                CheatTestBench.InitialState().SleeveBloom.WithOpenedWindow(2, 1, out string windowId));
            TilePositionMap map = CheatTestBench.CraftedOpening();
            TileId dealerDrawn = map.DrawnTile!.Value.Tile;
            map = map.Discard(0, dealerDrawn).RequireState(); // 黄金 phase=DISCARD：本人弃牌后

            CheatState activated = CheatSubmission.ActivateOffTurnSleeveBloom(
                state, map,
                CheatTestBench.Context(0, RoundPhase.Discard, SleeveBloomDrawKind.Normal),
                CheatTestBench.Economy(), CheatTestBench.Sleeve(), 2, "req-1", windowId).State!;
            Assert.Equal(30, activated.Ledger.BalanceOf(2));
            SleeveBloomOffer offer = activated.SleeveBloom.OffTurnOfferFor(2)!;
            Assert.Equal(TileZone.Wall, map.PositionOf(offer.FirstTile).Zone);
            Assert.Equal(TileZone.Wall, map.PositionOf(offer.SecondTile).Zone);

            CheatState reserved = CheatSubmission.ChooseOffTurnSleeveBloom(
                activated, map,
                CheatTestBench.Context(0, RoundPhase.Discard, SleeveBloomDrawKind.Normal),
                2, offer.OfferId, SleeveBloomKeep.Second).State!;
            SleeveBloomReservation reservation = reserved.SleeveBloom.ReservationFor(2)!;
            Assert.True(reservation.Tile.Equals(offer.SecondTile)); // 黄金 keep=SECOND：预留第二候选
            int reservedSlot = map.PositionOf(reservation.Tile).Index;
            Assert.True(reservedSlot >= map.WallPointer); // 黄金 wallIndex=1 ≥ 指针 0（同构关系）
            Assert.Equal(30, reserved.Ledger.BalanceOf(2)); // 选择不退款

            CheatTransition delivered = CheatSubmission.DeliverSleeveBloomReservation(
                reserved, map,
                CheatTestBench.Context(2, RoundPhase.Draw, SleeveBloomDrawKind.Normal),
                2);
            Assert.True(delivered.Success, delivered.Message);
            TilePositionMap moved = delivered.Map!;
            Assert.Equal(TileZone.Hand, moved.PositionOf(reservation.Tile).Zone); // 黄金 factsAfter：预留牌进手
            Assert.Equal(2, moved.PositionOf(reservation.Tile).Seat);
            Assert.Equal(DrawSource.Wall, moved.DrawnTile!.Value.Source);
            CheatTestBench.AssertConserved(moved);
            CheatEvent deliveryEvent = Assert.Single(delivered.Events);
            Assert.Equal(CheatEventKind.SleeveBloomReservationDelivered, deliveryEvent.Kind);

            // removeAt 同构：与黄金数值关系一致——指针不动、墙尾收缩一格
            Assert.Equal(map.WallPointer, moved.WallPointer);
            Assert.Equal(map.WallEndExclusive - 1, moved.WallEndExclusive);

            // removeAt 同构：预留槽前实体不动、预留槽后实体整体前移一格
            Assert.True(moved.TileAt(TilePosition.Wall(reservedSlot - 1)).Equals(map.TileAt(TilePosition.Wall(reservedSlot - 1))));
            Assert.True(moved.TileAt(TilePosition.Wall(reservedSlot)).Equals(map.TileAt(TilePosition.Wall(reservedSlot + 1))));

            // 后续抽牌序列与 V4 一致：预留槽前的首张照常被下一席摸走，前移后的后继实体紧随
            TileId frontTile = map.TileAt(TilePosition.Wall(reservedSlot - 1));
            TileId shiftedTile = map.TileAt(TilePosition.Wall(reservedSlot + 1));
            TilePositionMap nextDraw = moved.DrawFromWall(3).RequireState();
            Assert.Equal(TileZone.Hand, nextDraw.PositionOf(frontTile).Zone);
            TilePositionMap thirdDraw = nextDraw.DrawFromWall(0).RequireState();
            Assert.Equal(TileZone.Hand, thirdDraw.PositionOf(shiftedTile).Zone);
        }

        /// <summary>
        /// mv-reservation-delivery-then-beam：交付（removeAt、指针不动）→ 同次摸牌
        /// CheatWindow 冻结窗实体集合 → 窗口首槽对换 → 后续摸牌。黄金在交付前导出
        /// 完整输入布局（136 placements + 指针/王牌/河事实）；C# 以恒等 tileId 映射
        /// （V4 tileId 与 C# TileId 同为 0..135 物理实体空间）FromPlacements 重建同一张
        /// 牌桌——输入双射在任何动作执行前由黄金唯一定义，不依赖被测实现自身的输出。
        /// 重建后逐位核对激活 offer 的候选实体（candidateTileIds 0..7 全量）与归一化
        /// 槽位（candidateSlots+53），再以黄金 handTileId/beamGotTileId/nextDrawTileId/
        /// factsAfter 核对选择、后续摸牌与每个实体的最终容器。
        /// </summary>
        [Fact]
        public void DeliveryThenBeam_BindsToGoldenCandidateEntities()
        {
            JsonElement m5 = Case(Golden(), "tileMovement", "mv-reservation-delivery-then-beam");

            // ---- 黄金输入布局 → 恒等映射重建（wall[k]↔Wall(k+53)、deadWall[i]↔DeadWall(i)、
            //      replacement[i]↔DeadWallReplacement(i)，手牌/河按座位+下标） ----
            var placements = new List<TilePlacement>();
            foreach (JsonElement p in m5.GetProperty("inputPlacements").EnumerateArray())
            {
                string container = Str(p, "container");
                int index = Int(p, "index");
                TilePosition position = container switch
                {
                    "hand" => TilePosition.Hand(Int(p, "seat"), index),
                    "discard" => TilePosition.Discard(Int(p, "seat"), index),
                    "wall" => TilePosition.Wall(WallSlotOffset + index),
                    "deadWall" => TilePosition.DeadWall(index),
                    "replacement" => TilePosition.DeadWallReplacement(index),
                    _ => throw new InvalidOperationException("unknown input container: " + container)
                };
                placements.Add(new TilePlacement(new TileId(Int(p, "tileId")), position));
            }

            TilePosition GoldenInputSlot(int tileId) => placements.Single(pl => pl.Tile.Value == tileId).Position;
            Assert.Equal(-1, Int(m5, "inputDrawnTileTileId")); // 交付时点=安静手（已弃摸牌）
            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                WallSlotOffset + Int(m5, "inputWallPointer"),
                WallSlotOffset + Int(m5, "inputWallEndExclusive"),
                Int(m5, "inputRinshanPointer"),
                new LastDiscardEvidence(
                    new TileId(Int(m5, "inputLastDiscardTileId")),
                    GoldenInputSlot(Int(m5, "inputLastDiscardTileId"))),
                0,
                null,
                Ints(m5, "inputDoraIndicatorTileIds").Select(d => new TileId(d)).ToArray(),
                Int(m5, "inputKanCount"));

            // 重建守恒 + 黄金实体落在黄金输入槽位（双射在动作前建立）
            CheatTestBench.AssertConserved(map);
            Assert.Equal(WallSlotOffset + Int(m5, "inputWallPointer"), map.WallPointer);
            Assert.Equal(WallSlotOffset + Int(m5, "inputWallEndExclusive"), map.WallEndExclusive);
            Assert.Equal(Int(m5, "inputKanCount"), map.KanCount);
            Assert.Equal(Int(m5, "inputLastDiscardTileId"), map.LastDiscard!.Value.Tile.Value);
            Assert.Equal(Int(m5, "inputLastDiscardSeat"), map.LastDiscard.Value.Position.Seat);
            Assert.True(map.TileAt(TilePosition.Wall(WallSlotOffset)).Value == Int(m5, "firstUndrawnTileId"),
                "rebuilt wall front must hold the golden firstUndrawn entity");
            Assert.True(map.TileAt(TilePosition.Wall(WallSlotOffset + 1)).Value == Int(m5, "reservedTileId"),
                "rebuilt wall[1] must hold the golden reserved entity");
            Assert.True(map.TileAt(TilePosition.Wall(WallSlotOffset + 2)).Value == Int(m5, "successorTileId"),
                "rebuilt wall[2] must hold the golden successor entity");

            // ---- 重放：预留注册 → 交付（removeAt） → 同次摸牌 beam 激活 ----
            int seat = Int(m5, "inputSeat");
            CheatState baseState = CheatTestBench.InitialState();
            CheatState reserved = baseState.WithSleeveBloom(baseState.SleeveBloom.WithReservation(
                new SleeveBloomReservation(seat, "bloom-offturn-1-x", new TileId(Int(m5, "reservedTileId")), null, 0)));
            CheatTransition delivery = CheatSubmission.DeliverSleeveBloomReservation(
                reserved, map,
                CheatTestBench.Context(seat, RoundPhase.Draw, SleeveBloomDrawKind.Normal),
                seat);
            Assert.True(delivery.Success, delivery.Message);
            TilePositionMap delivered = delivery.Map!;
            Assert.Equal(WallSlotOffset + Int(m5, "wallPointerBeforeDelivery"), map.WallPointer);
            Assert.Equal(WallSlotOffset + Int(m5, "wallPointerAfterDelivery"), delivered.WallPointer);
            Assert.Equal(WallSlotOffset + Int(m5, "wallEndExclusiveAfterDelivery"), delivered.WallEndExclusive);
            int deliveredTileId = Int(m5, "deliveredTileId");
            Assert.Equal(Int(m5, "reservedTileId"), deliveredTileId);
            TilePosition deliveredPos = delivered.PositionOf(new TileId(deliveredTileId));
            Assert.True(deliveredPos.Zone == TileZone.Hand && deliveredPos.Seat == seat, // 黄金：预留牌进手
                "delivered reservation tile must sit in the owner's hand");

            CheatTurnContext beamContext = CheatTestBench.Context(seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);
            CheatState beamed = CheatSubmission.ActivateBeamSwap(
                delivery.State!, delivered, beamContext,
                CheatTestBench.Economy(), CheatTestBench.Sleeve(), "req-2", seat).State!;
            BeamSwapOffer offer = beamed.BeamSwap.Offer!;

            // 候选逐位对照：实体 ID 与归一化槽位全量消费黄金数组（任一被替换即失败）
            int[] goldenCandidates = Ints(m5, "candidateTileIds");
            int[] goldenSlots = Ints(m5, "candidateSlots");
            Assert.Equal(8, goldenCandidates.Length);
            Assert.Equal(goldenCandidates.Length, offer.Candidates.Count);
            Assert.Equal(goldenSlots.Length, offer.Candidates.Count);
            Assert.DoesNotContain(Int(m5, "reservedTileId"), goldenCandidates); // 已交付实体不在窗（黄金自洽）
            for (int i = 0; i < offer.Candidates.Count; i++)
            {
                Assert.True(offer.Candidates[i].Tile.Value == goldenCandidates[i],
                    "candidate " + i + " entity mismatch: golden " + goldenCandidates[i] +
                    " vs C# " + offer.Candidates[i].Tile.Value);
                Assert.True(offer.Candidates[i].Slot.Kind == BeamSwapSlotKind.WallSlot);
                Assert.True(offer.Candidates[i].Slot.Index == WallSlotOffset + goldenSlots[i],
                    "candidate " + i + " slot mismatch: golden " + (WallSlotOffset + goldenSlots[i]) +
                    " vs C# " + offer.Candidates[i].Slot.Index);
            }

            // ---- 选择：黄金 handTile 与黄金窗首候选槽 ----
            TileId handTile = new TileId(Int(m5, "handTileId"));
            Assert.True(offer.EligibleHandTileIds.Any(t => t.Equals(handTile)),
                "golden handTile must be beam-eligible in the rebuilt map");
            CheatTransition swapped = CheatSubmission.ChooseBeamSwap(
                beamed, delivered, beamContext, seat, offer.OfferId,
                BeamSwapAction.Swap, handTile, BeamSwapSlot.Wall(WallSlotOffset + goldenSlots[0]));
            Assert.True(swapped.Success, swapped.Message);
            TilePositionMap afterSwap = swapped.Map!;
            TilePosition beamGotPos = afterSwap.PositionOf(new TileId(Int(m5, "beamGotTileId")));
            Assert.True(beamGotPos.Zone == TileZone.Hand && beamGotPos.Seat == seat, // 黄金：beamGot 进手
                "golden beamGot entity must move into the owner's hand");
            Assert.True(afterSwap.PositionOf(handTile).Equals(TilePosition.Wall(WallSlotOffset + goldenSlots[0])),
                "golden handTile must land on the golden first candidate slot");
            Assert.Equal(delivered.WallPointer, afterSwap.WallPointer);
            CheatTestBench.AssertConserved(afterSwap);

            // ---- 后续：弃 beamGot → 下一席普通摸牌摸到的正是换入实体（黄金 nextDrawTileId） ----
            TilePositionMap quiet = afterSwap.Discard(seat, new TileId(Int(m5, "beamGotTileId"))).RequireState();
            TilePositionMap nextDraw = quiet.DrawFromWall((seat + 1) % 4).RequireState();
            TilePosition nextPos = nextDraw.PositionOf(new TileId(Int(m5, "nextDrawTileId")));
            Assert.True(nextPos.Zone == TileZone.Hand && nextPos.Seat == (seat + 1) % 4,
                "golden nextDrawTileId must be the next seat's actual draw");
            CheatTestBench.AssertConserved(nextDraw);

            // factsAfter 逐项容器对照：hand/discard 按座位，wall/deadWall/replacement 按归一化槽位
            foreach (JsonElement fact in m5.GetProperty("factsAfter").EnumerateArray())
            {
                int factTileId = Int(fact, "tileId");
                string container = Str(fact, "container");
                bool seatless = container == "wall" || container == "deadWall" || container == "replacement";
                if (seatless)
                {
                    Assert.Equal(JsonValueKind.Null, fact.GetProperty("seat").ValueKind);
                    Assert.Equal(JsonValueKind.Number, fact.GetProperty("index").ValueKind);
                }
                else
                {
                    Assert.Equal(JsonValueKind.Number, fact.GetProperty("seat").ValueKind);
                    Assert.Equal(JsonValueKind.Null, fact.GetProperty("index").ValueKind);
                }

                TilePosition pos = nextDraw.PositionOf(new TileId(factTileId));
                string label = "fact tile " + factTileId + " expected in " + container;
                switch (container)
                {
                    case "hand":
                        Assert.True(pos.Zone == TileZone.Hand && pos.Seat == Int(fact, "seat"), label);
                        break;
                    case "discard":
                        Assert.True(pos.Zone == TileZone.Discard && pos.Seat == Int(fact, "seat"), label);
                        break;
                    case "wall":
                        Assert.True(pos.Zone == TileZone.Wall && pos.Index == WallSlotOffset + Int(fact, "index"), label);
                        break;
                    case "deadWall":
                        Assert.True(pos.Zone == TileZone.DeadWall && pos.Index == Int(fact, "index"), label);
                        break;
                    case "replacement":
                        Assert.True(pos.Zone == TileZone.DeadWallReplacement && pos.Index == Int(fact, "index"), label);
                        break;
                    default:
                        throw new InvalidOperationException("unknown fact container: " + container);
                }
            }
        }

        /// <summary>
        /// mv-rinshan-and-drawn-swap 与 mv-swap-hand-with-dead-wall 的同构重放：
        /// 岭上开花夹具本身携带 drawRinshan 事实（drawn 进手、next 留 deadWall[1]、
        /// 墙尾替补进 replacement[0]）；偷梁换柱 deadWall 窗口把 closed 与 deadWall[1]
        /// 原地对换、replacement 槽参与、显宝牌槽（deadWall[4]）永远不是候选。
        /// </summary>
        [Fact]
        public void TileMovement_DeadWallLaws_ReplayThroughBeamSwapEntrypoint()
        {
            JsonElement root = Golden();
            JsonElement m3 = Case(root, "tileMovement", "mv-rinshan-and-drawn-swap");
            JsonElement m4 = Case(root, "tileMovement", "mv-swap-hand-with-dead-wall");

            // 黄金结构：M3 的岭上事实与 M4 的对换事实。
            // V4 记账：drawRinshan 把 deadWall[0] 记入 deadWall 容器（slot1 字段证实对换后
            // 该槽就是岭上牌），next 岭上牌在对换后进手。
            Assert.Equal("deadWall", FactContainer(m3, "factsAfter", Int(m3, "rinshanTileId")));
            Assert.Equal(Int(m3, "rinshanTileId"), Int(m3, "deadWallSlot1AfterSwap"));
            Assert.Equal("hand", FactContainer(m3, "factsAfter", Int(m3, "nextRinshanTileId")));
            Assert.Equal(Int(m3, "wallEndExclusiveBefore") - 1, Int(m3, "wallEndExclusiveAfter"));
            Assert.Equal("deadWall", FactContainer(m4, "factsAfter", Int(m4, "handTileId")));
            Assert.Equal("hand", FactContainer(m4, "factsAfter", Int(m4, "gotTileId")));
            // m4 是复合夹具：hand↔deadWall[1] 之后又 hand↔replacement[0]（两个拒绝探针不动
            // 状态），所以原 replacement 牌已进手、replacement[0] 躺着第二手牌
            Assert.Equal("hand", FactContainer(m4, "factsAfter", Int(m4, "replacementTileIdBefore")));
            Assert.Equal("replacement", FactContainer(m4, "factsAfter", Int(m4, "replacementSlot0AfterSwap")));

            // C# 同构重放：rinshan 夹具事实
            TilePositionMap map = CheatTestBench.OpeningAfterRinshanDraw(Seat, out TileId drawnTile, out TileId nextRinshanTile);
            Assert.True(map.PositionOf(drawnTile).Zone == TileZone.Hand);
            Assert.True(map.TileAt(TilePosition.DeadWall(1)).Equals(nextRinshanTile));

            CheatTurnContext ctx = CheatTestBench.Context(Seat, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan);
            CheatState activated = CheatSubmission.ActivateBeamSwap(
                CheatTestBench.InitialState(), map, ctx, CheatTestBench.Economy(), CheatTestBench.Sleeve(), "req-1", Seat).State!;
            TileId closedTile = activated.BeamSwap.Offer!.EligibleHandTileIds[0];
            TilePosition handSlot = map.PositionOf(closedTile);

            // deadWall[1] 对换（V4 deadWall 无偏移）
            CheatTransition deadWallSwap = CheatSubmission.ChooseBeamSwap(
                activated, map, ctx, Seat, activated.BeamSwap.Offer!.OfferId,
                BeamSwapAction.Swap, closedTile, BeamSwapSlot.DeadWall(1));
            Assert.True(deadWallSwap.Success, deadWallSwap.Message);
            Assert.True(deadWallSwap.Map!.PositionOf(nextRinshanTile).Equals(handSlot));
            Assert.True(deadWallSwap.Map.PositionOf(closedTile).Equals(TilePosition.DeadWall(1)));
            CheatTestBench.AssertConserved(deadWallSwap.Map);

            // replacement 槽对换
            TileId replacementTile = map.TileAt(TilePosition.DeadWallReplacement(0));
            CheatTransition replacementSwap = CheatSubmission.ChooseBeamSwap(
                activated, map, ctx, Seat, activated.BeamSwap.Offer!.OfferId,
                BeamSwapAction.Swap, closedTile, BeamSwapSlot.Replacement(0));
            Assert.True(replacementSwap.Success, replacementSwap.Message);
            Assert.True(replacementSwap.Map!.PositionOf(replacementTile).Equals(handSlot));
            Assert.True(replacementSwap.Map.PositionOf(closedTile).Equals(TilePosition.DeadWallReplacement(0)));
            CheatTestBench.AssertConserved(replacementSwap.Map);

            // 显宝牌槽（deadWall[4]）被拒：V4 IllegalArgumentException；C# 类型化 CandidateNotFound
            CheatTransition doraSlot = CheatSubmission.ChooseBeamSwap(
                activated, map, ctx, Seat, activated.BeamSwap.Offer!.OfferId,
                BeamSwapAction.Swap, closedTile, BeamSwapSlot.DeadWall(4));
            Assert.False(doraSlot.Success);
            Assert.Equal(CheatErrorCode.CandidateNotFound, doraSlot.Error);
            Assert.Equal(4, Int(m4, "doraSlotIndex"));
        }
    }
}
