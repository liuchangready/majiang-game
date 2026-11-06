using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.TableRuntime;
using Mahjong.TableService.Tests;
    using WireDecisionView = Mahjong.Contracts.Wire.DecisionView;
    using WirePublicBoardView = Mahjong.Contracts.Wire.PublicBoardView;
    using WireMeldView = Mahjong.Contracts.Wire.MeldView;
    using WireDiscardView = Mahjong.Contracts.Wire.DiscardView;
    using WireLegalActionView = Mahjong.Contracts.Wire.LegalActionView;
    using WirePlayerPublicView = Mahjong.Contracts.Wire.PlayerPublicView;
    using WireReceiptStatus = Mahjong.Contracts.Wire.ReceiptStatus;

namespace Mahjong.TableService.PersistenceWorker
{
    /// <summary>
    /// D-R2-01 remaining acceptance: a REAL cross-process crash chain for a legally
    /// claimed opening discard. This is a TEST WORKER MODE only — it starts a real
    /// TableService host in its own process (the existing public Build seam with the
    /// deterministic N1a fixture), drives real strict-WSS admissions/discard/claim,
    /// persists a post-claim checkpoint through the host's own actor, and then blocks with
    /// both sessions LIVE so the runner can kill the process. A different worker process
    /// then cold-recovers the SAME room from the SAME PostgreSQL with a DIFFERENT deal and
    /// verifies the persisted opening fact, public history, receipt, ledger and 136 tiles.
    /// </summary>
    internal static class ClaimedOpeningWorker
    {
        internal static async Task<int> RunHostAsync(Dictionary<string, string> options)
        {
            string room = Program.Require(options, "room");
            string claim = Program.Require(options, "claim").ToLowerInvariant();
            if (claim != "chi" && claim != "pon")
            {
                throw new ArgumentException("claimed-host supports --claim chi|pon.");
            }

            string statePath = Program.Require(options, "out");
            string keysDirectory = Program.Require(options, "keys");
            _ = Program.Require(options, "ca-root");
            string tlsPfx = Program.Require(options, "tls-pfx");
            string tlsPassword = Program.Optional(options, "tls-password") ?? string.Empty;
            bool holdOpen = options.ContainsKey("hold-open");

            WorkerTicketSigner signer = await WorkerTicketSigner.LoadAsync(keysDirectory).ConfigureAwait(false);
            Mahjong.TableService.TableServiceOptions serviceOptions = BuildServiceOptions(tlsPfx, tlsPassword);
            Mahjong.TableService.TableServiceHostRuntime runtime = Mahjong.TableService.TableServiceWebHost.Build(
                serviceOptions,
                new Mahjong.TableService.StaticTableTicketKeyProvider(
                    new[] { (signer.PublicPem, (string?)signer.Kid) },
                    DateTimeOffset.UtcNow),
                IPAddress.Loopback,
                0,
                ClaimRoundSource(claim, poison: false));
            await runtime.StartAsync().ConfigureAwait(false);

            string url = "wss://127.0.0.1:" + runtime.GetBoundPort().ToString(CultureInfo.InvariantCulture) +
                         "/v1/table?room=" + Uri.EscapeDataString(room);
            TableTestClient zero = await Program.ConnectTableClientAsync(options, url).ConfigureAwait(false);
            Welcome welcomeZero = await zero
                .AdmitAsync(signer.SignTicket("claimed-" + claim + "-0", room, 0))
                .ConfigureAwait(false);
            TableTestClient one = await Program.ConnectTableClientAsync(options, url).ConfigureAwait(false);
            Welcome welcomeOne = await one
                .AdmitAsync(signer.SignTicket("claimed-" + claim + "-1", room, 1))
                .ConfigureAwait(false);

            int target = claim == "chi" ? 8 : 127;
            PlayerSnapshot openingSnapshot = await WaitDecisionAsync(
                zero,
                decision => decision.Actions.Any(action =>
                    action.ActionType == (uint)ProtocolActionType.DiscardTile &&
                    action.TileIds.Contains((uint)target))).ConfigureAwait(false);
            WireDecisionView openingDecision = openingSnapshot.PrivateView!.Decision!;
            WireLegalActionView openingAction = openingDecision.Actions.First(action =>
                action.ActionType == (uint)ProtocolActionType.DiscardTile &&
                action.TileIds.Contains((uint)target));
            string openingRequest = TableWire.RandomId("req");
            Envelope openingReceipt = await Program.SendAndAwaitReceiptAsync(
                zero,
                welcomeZero,
                openingRequest,
                TableWire.SubmitActionPayload(
                    openingSnapshot.RoundId,
                    openingDecision.DecisionId,
                    openingDecision.ControlEpoch,
                    openingAction.ActionType,
                    openingAction.TileIds.ToArray()),
                openingSnapshot.StateVersion).ConfigureAwait(false);
            RequireApplied(openingReceipt, "opening discard");

            ProtocolActionType claimAction = claim == "chi"
                ? ProtocolActionType.DeclareChi
                : ProtocolActionType.DeclarePon;
            PlayerSnapshot claimSnapshot = await WaitDecisionAsync(
                one,
                decision => decision.Actions.Any(action => action.ActionType == (uint)claimAction))
                .ConfigureAwait(false);
            WireDecisionView claimDecision = claimSnapshot.PrivateView!.Decision!;
            uint[] meldTiles = claim == "chi"
                ? new uint[] { 8, 0, 4 }
                : new uint[] { 127, 124, 125 };
            string claimRequest = TableWire.RandomId("req");
            Envelope claimReceipt = await Program.SendAndAwaitReceiptAsync(
                one,
                welcomeOne,
                claimRequest,
                TableWire.SubmitActionPayload(
                    claimSnapshot.RoundId,
                    claimDecision.DecisionId,
                    claimDecision.ControlEpoch,
                    (uint)claimAction,
                    meldTiles),
                claimSnapshot.StateVersion).ConfigureAwait(false);
            RequireApplied(claimReceipt, "claim");
            ulong claimVersion = claimReceipt.Receipt.CommandVersion;

            Envelope claimedEnvelope = await zero.ReadUntilAsync(
                message => message.PlayerUpdate?.Snapshot is PlayerSnapshot snapshot &&
                           snapshot.StateVersion >= claimVersion,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            PlayerSnapshot claimedSnapshot = claimedEnvelope.PlayerUpdate!.Snapshot;
            WirePublicBoardView claimedBoard = claimedSnapshot.PublicView!.Board;
            WirePlayerPublicView claimedSeat0 = claimedBoard.Players.First(player => player.Seat == 0);
            WirePlayerPublicView claimedSeat1 = claimedBoard.Players.First(player => player.Seat == 1);
            if (claimedSeat0.Discards.Any(discard => discard.TileId == (uint)target))
            {
                throw new InvalidOperationException("The claimed opening discard is still in seat 0's river.");
            }

            WireMeldView meld = claimedSeat1.Melds.SingleOrDefault() ??
                throw new InvalidOperationException("The claimant carries no singular meld after the claim.");
            if ((int)meld.Type != (claim == "chi" ? (int)MeldType.Chi : (int)MeldType.Pon))
            {
                throw new InvalidOperationException("The claimant meld type does not match the claim.");
            }

            Mahjong.TableService.PostgresTableHostRegistry registry =
                (Mahjong.TableService.PostgresTableHostRegistry)(runtime.HostRegistry ??
                    throw new InvalidOperationException("The host has no durable registry."));
            if (!registry.TryGet(room, out Mahjong.TableService.RoomHost? host))
            {
                throw new InvalidOperationException("The serving room is not registered after the claim.");
            }

            TableCheckpoint checkpoint = await PersistPostClaimCheckpointAsync(host!, room, claimVersion)
                .ConfigureAwait(false);
            RoundRecoveryState state = RoundRecoveryCodec.Decode(checkpoint.StatePayload);
            Dictionary<string, object?> openingFact = ReadOpeningFact(checkpoint, state, 0, target);
            int appliedRows = await AppliedReceiptCountAsync(room).ConfigureAwait(false);
            if (appliedRows != 2)
            {
                throw new InvalidOperationException(
                    "Expected exactly two APPLIED receipts after discard+claim, got " + appliedRows + ".");
            }

            var evidence = new Dictionary<string, object?>
            {
                ["room"] = room,
                ["claim"] = claim,
                ["processId"] = Environment.ProcessId,
                ["bootId"] = registry.ProcessBootId,
                ["hostPort"] = runtime.GetBoundPort(),
                ["strictWss"] = true,
                ["claimVersion"] = (long)claimVersion,
                ["checkpointVersion"] = (long)checkpoint.StateVersion.Value,
                ["claimRequest"] = claimRequest,
                ["claimScope"] = welcomeOne.Scope.ScopeId,
                ["openingRequest"] = openingRequest,
                ["openingScope"] = welcomeZero.Scope.ScopeId,
                ["openingTile"] = target,
                ["meldType"] = (int)meld.Type,
                ["meldTiles"] = meld.TileIds.Select(tile => (long)tile).ToList(),
                ["openingFact"] = openingFact,
                ["history"] = CaptureHistory(claimedSnapshot),
                ["claimedSeat0Discards"] = claimedSeat0.Discards.Count,
                ["claimedSeat1Melds"] = claimedSeat1.Melds.Count,
                ["appliedRows"] = appliedRows
            };

            if (holdOpen)
            {
                await Program.WriteJsonAsync(statePath, evidence).ConfigureAwait(false);
                Console.WriteLine(
                    "claimed-host ready pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) +
                    " boot=" + registry.ProcessBootId +
                    " holding-session-open");
                Console.Out.Flush();
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                return Program.ExitOk;
            }

            await zero.CloseCleanAsync().ConfigureAwait(false);
            await one.CloseCleanAsync().ConfigureAwait(false);
            await runtime.StopAsync().ConfigureAwait(false);
            await Program.WriteJsonAsync(statePath, evidence).ConfigureAwait(false);
            Console.WriteLine("claimed-host ok version=" + claimVersion.ToString(CultureInfo.InvariantCulture));
            return Program.ExitOk;
        }

        internal static async Task<int> RunRecoverAsync(Dictionary<string, string> options)
        {
            string room = Program.Require(options, "room");
            string statePath = Program.Require(options, "state");
            string outputPath = Program.Require(options, "out");
            string keysDirectory = Program.Require(options, "keys");
            _ = Program.Require(options, "ca-root");
            string tlsPfx = Program.Require(options, "tls-pfx");
            string tlsPassword = Program.Optional(options, "tls-password") ?? string.Empty;

            JsonElement before = await Program.ReadJsonAsync(statePath).ConfigureAwait(false);
            string claim = before.GetProperty("claim").GetString()!;
            int target = before.GetProperty("openingTile").GetInt32();
            ulong claimVersion = (ulong)before.GetProperty("claimVersion").GetInt64();
            string claimRequest = before.GetProperty("claimRequest").GetString()!;
            string claimScope = before.GetProperty("claimScope").GetString()!;
            int oldPid = before.GetProperty("processId").GetInt32();
            string oldBoot = before.GetProperty("bootId").GetString()!;

            WorkerTicketSigner signer = await WorkerTicketSigner.LoadAsync(keysDirectory).ConfigureAwait(false);
            Mahjong.TableService.TableServiceOptions serviceOptions = BuildServiceOptions(tlsPfx, tlsPassword);
            Mahjong.TableService.TableServiceHostRuntime runtime = Mahjong.TableService.TableServiceWebHost.Build(
                serviceOptions,
                new Mahjong.TableService.StaticTableTicketKeyProvider(
                    new[] { (signer.PublicPem, (string?)signer.Kid) },
                    DateTimeOffset.UtcNow),
                IPAddress.Loopback,
                0,
                ClaimRoundSource(claim, poison: true));
            await runtime.StartAsync().ConfigureAwait(false);

            string url = "wss://127.0.0.1:" + runtime.GetBoundPort().ToString(CultureInfo.InvariantCulture) +
                         "/v1/table?room=" + Uri.EscapeDataString(room);
            TableTestClient zero = await Program.ConnectTableClientAsync(options, url).ConfigureAwait(false);
            Welcome welcomeZero = await zero
                .AdmitAsync(signer.SignTicket("claimed-" + claim + "-0", room, 0))
                .ConfigureAwait(false);
            TableTestClient one = await Program.ConnectTableClientAsync(options, url).ConfigureAwait(false);
            Welcome welcomeOne = await one
                .AdmitAsync(signer.SignTicket("claimed-" + claim + "-1", room, 1))
                .ConfigureAwait(false);

            ulong recoveredVersion = welcomeZero.Baseline.StateVersion;
            if (recoveredVersion < claimVersion)
            {
                throw new InvalidOperationException(
                    "The recovered baseline is older than the post-claim commit (" +
                    recoveredVersion + " < " + claimVersion + ").");
            }

            Envelope recoveredEnvelope = await zero.ReadUntilAsync(
                message => message.PlayerUpdate?.Snapshot is PlayerSnapshot snapshot &&
                           snapshot.StateVersion >= claimVersion,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            PlayerSnapshot recoveredSnapshot = recoveredEnvelope.PlayerUpdate!.Snapshot;
            WirePublicBoardView board = recoveredSnapshot.PublicView!.Board;
            WirePlayerPublicView seat0 = board.Players.First(player => player.Seat == 0);
            WirePlayerPublicView seat1 = board.Players.First(player => player.Seat == 1);
            bool seat0RiverHasOpening = seat0.Discards.Any(discard => discard.TileId == (uint)target);
            if (seat0RiverHasOpening)
            {
                throw new InvalidOperationException("The recovered river still holds the claimed opening discard.");
            }

            WireMeldView meld = seat1.Melds.SingleOrDefault() ??
                throw new InvalidOperationException("The recovered claimant has no singular meld.");
            long expectedMeldType = before.GetProperty("meldType").GetInt64();
            if ((long)meld.Type != expectedMeldType)
            {
                throw new InvalidOperationException("The recovered meld type changed across the crash.");
            }

            List<uint> expectedMeldTiles = before.GetProperty("meldTiles")
                .EnumerateArray()
                .Select(value => (uint)value.GetInt64())
                .OrderBy(value => value)
                .ToList();
            List<uint> actualMeldTiles = meld.TileIds.OrderBy(value => value).ToList();
            if (!expectedMeldTiles.SequenceEqual(actualMeldTiles))
            {
                throw new InvalidOperationException("The recovered meld tiles changed across the crash.");
            }

            AssertHistoryExtends(before.GetProperty("history"), recoveredSnapshot);

            Mahjong.TableService.PostgresTableHostRegistry registry =
                (Mahjong.TableService.PostgresTableHostRegistry)(runtime.HostRegistry ??
                    throw new InvalidOperationException("The recovering host has no durable registry."));
            if (!registry.TryGet(room, out Mahjong.TableService.RoomHost? host) || host == null)
            {
                throw new InvalidOperationException("The recovered room is not registered.");
            }

            TableCheckpoint checkpoint = await host.Store.LoadCheckpointAsync(room, CancellationToken.None)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("The post-claim checkpoint is missing.");
            RoundRecoveryState checkpointState = RoundRecoveryCodec.Decode(checkpoint.StatePayload);
            Dictionary<string, object?> checkpointFact = ReadOpeningFact(checkpoint, checkpointState, 0, target);
            AssertOpeningFact(before.GetProperty("openingFact"), checkpointFact);

            await one.SendAsync(TableWire.ReceiptQuery(welcomeOne, claimScope, claimRequest)).ConfigureAwait(false);
            Envelope lookup = await one.ReadUntilAsync(
                message => message.MessageType == MessageType.ReceiptLookup,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (lookup.ReceiptLookup.Status != LookupStatus.Found ||
                lookup.ReceiptLookup.Receipt.CommandVersion != claimVersion)
            {
                throw new InvalidOperationException(
                    "The post-claim receipt was not reconciled (status " +
                    lookup.ReceiptLookup.Status + " version " +
                    lookup.ReceiptLookup.Receipt.CommandVersion + ").");
            }

            int claimLedgerRows = await Program.CountLedgerRowAsync(
                claimRequest, claimScope, (long)claimVersion).ConfigureAwait(false);
            if (claimLedgerRows != 1)
            {
                throw new InvalidOperationException(
                    "The post-claim receipt ledger row was duplicated or rewritten (" + claimLedgerRows + ").");
            }

            int appliedBefore = await AppliedReceiptCountAsync(room).ConfigureAwait(false);
            if (appliedBefore != 2)
            {
                throw new InvalidOperationException(
                    "Expected exactly two APPLIED receipts before continued play, got " + appliedBefore + ".");
            }

            ulong lastVersion = recoveredSnapshot.StateVersion;
            var seatClients = new Dictionary<int, TableTestClient> { [0] = zero, [1] = one };
            var extraClients = new List<TableTestClient>();
            Envelope actionReceipt = await PlayOneAuthoritativeDiscardAsync(
                runtime, signer, room, zero, lastVersion, seatClients, extraClients, options).ConfigureAwait(false);

            if (actionReceipt.Receipt.Status != WireReceiptStatus.Applied || actionReceipt.Receipt.Replayed)
            {
                throw new InvalidOperationException("The post-recovery real discard was not a fresh APPLIED receipt.");
            }

            lastVersion = actionReceipt.Receipt.CommandVersion;
            await zero.ReadUntilAsync(
                message => message.PlayerUpdate?.Snapshot is PlayerSnapshot snapshot &&
                           snapshot.StateVersion >= lastVersion,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            TableCheckpoint postPlay = await PersistPostPlayCheckpointAsync(host, room, lastVersion)
                .ConfigureAwait(false);
            RoundRecoveryState postState = RoundRecoveryCodec.Decode(postPlay.StatePayload);
            int tileCount = CountAllTiles(postState.Tiles);
            int unassigned = postState.Tiles.CountAt(TileZone.Unassigned);
            if (tileCount != 136 || unassigned != 0)
            {
                throw new InvalidOperationException(
                    "Post-play physical conservation failed (tiles=" + tileCount + " unassigned=" + unassigned + ").");
            }

            if (postState.Tiles.TilesInZone(TileZone.Discard, 0).Any(tile => tile.Value == target))
            {
                throw new InvalidOperationException("Continued play moved the claimed opening discard back into the river.");
            }

            Dictionary<string, object?> postFact = ReadOpeningFact(postPlay, postState, 0, target);
            AssertOpeningFact(before.GetProperty("openingFact"), postFact);
            int appliedAfter = await AppliedReceiptCountAsync(room).ConfigureAwait(false);
            if (appliedAfter != 3)
            {
                throw new InvalidOperationException(
                    "Expected exactly three APPLIED receipts after continued play, got " + appliedAfter + ".");
            }

            if (Environment.ProcessId == oldPid)
            {
                throw new InvalidOperationException("The recovery process is the SAME process as the crashed host.");
            }

            if (string.Equals(registry.ProcessBootId, oldBoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The recovery process reports the SAME boot id as the crashed host.");
            }

            foreach (TableTestClient extra in extraClients)
            {
                try { await extra.CloseCleanAsync().ConfigureAwait(false); } catch (Exception) { }
            }

            await zero.CloseCleanAsync().ConfigureAwait(false);
            await one.CloseCleanAsync().ConfigureAwait(false);
            await runtime.StopAsync().ConfigureAwait(false);

            await Program.WriteJsonAsync(outputPath, new Dictionary<string, object?>
            {
                ["room"] = room,
                ["claim"] = claim,
                ["processId"] = Environment.ProcessId,
                ["bootId"] = registry.ProcessBootId,
                ["oldProcessId"] = oldPid,
                ["oldBootId"] = oldBoot,
                ["strictWss"] = true,
                ["recoveredVersion"] = (long)recoveredVersion,
                ["claimVersion"] = (long)claimVersion,
                ["claimReceiptFound"] = true,
                ["claimReceiptVersion"] = (long)lookup.ReceiptLookup.Receipt.CommandVersion,
                ["claimLedgerRows"] = claimLedgerRows,
                ["appliedLedgerRows"] = appliedAfter,
                ["postActionApplied"] = true,
                ["postActionVersion"] = (long)lastVersion,
                ["tileCount"] = tileCount,
                ["unassignedCount"] = unassigned,
                ["openingFactUnchanged"] = true,
                ["seat0RiverHasOpening"] = false,
                ["historyExtended"] = true,
                ["meldType"] = (long)meld.Type,
                ["meldTiles"] = actualMeldTiles.Select(value => (long)value).ToList()
            }).ConfigureAwait(false);
            Console.WriteLine(
                "claimed-recover ok recoveredVersion=" + recoveredVersion.ToString(CultureInfo.InvariantCulture) +
                " pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) +
                " boot=" + registry.ProcessBootId);
            return Program.ExitOk;
        }

        private static Mahjong.TableService.TableServiceOptions BuildServiceOptions(
            string tlsPfx,
            string tlsPassword)
        {
            var options = new Mahjong.TableService.TableServiceOptions
            {
                StoreMode = Mahjong.TableService.TableHostStoreMode.Postgres,
                PostgresConnectionString = Program.RequireRuntimeConnection(),
                PostgresPayloadKeyBase64 = RequirePayloadKey(),
                PostgresApplyMigrations = false,
                HandshakeTimeoutMs = 2000,
                HeartbeatIntervalMs = 500,
                IdleDeadMs = 60_000,
                RoomTickIntervalMs = 50,
                LeaseTtlSeconds = 5,
                HttpPort = 0,
                ServerCertificate = LoadServerCertificate(tlsPfx, tlsPassword)
            };
            return options;
        }

        private static string RequirePayloadKey()
        {
            string? key = Environment.GetEnvironmentVariable("MAHJONG_P54B_TEST_PG_KEY");
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("MAHJONG_P54B_TEST_PG_KEY is required.");
            }

            return key;
        }

        private static X509Certificate2 LoadServerCertificate(string path, string password)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The TLS PFX file does not exist.", path);
            }

            return new X509Certificate2(File.ReadAllBytes(path), password, X509KeyStorageFlags.Exportable);
        }

        private static ITableRoundSource ClaimRoundSource(string claim, bool poison)
        {
            return new FixedRoundSource(roomId =>
            {
                int target = claim == "chi" ? 8 : 127;
                int drawn = poison ? 108 : target;
                IEnumerable<int> hand = poison ? Enumerable.Range(109, 13) : Enumerable.Range(108, 13);
                int[] pair = claim == "chi" ? new[] { 0, 4 } : new[] { 124, 125 };
                var fixture = new N1aTable { Dealer = 0 };
                fixture.Hand(0, hand.Select(value => new TileId(value)).ToArray());
                fixture.Drawn(0, new TileId(drawn));
                fixture.Hand(1, pair.Select(value => new TileId(value)).ToArray());
                fixture.Hand(2, Enumerable.Range(20, 13).Select(value => new TileId(value)).ToArray());
                fixture.Hand(3, Enumerable.Range(40, 13).Select(value => new TileId(value)).ToArray());
                return fixture.Build(roomId + "-r1");
            });
        }

        private static async Task<PlayerSnapshot> WaitDecisionAsync(
            TableTestClient client,
            Func<WireDecisionView, bool> predicate)
        {
            Envelope envelope = await client.ReadUntilAsync(
                message => message.PlayerUpdate?.Snapshot?.PrivateView?.Decision is WireDecisionView decision &&
                           predicate(decision),
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return envelope.PlayerUpdate!.Snapshot;
        }

        private static void RequireApplied(Envelope envelope, string what)
        {
            if (envelope.Receipt.Status != WireReceiptStatus.Applied || envelope.Receipt.Replayed)
            {
                throw new InvalidOperationException(
                    "The " + what + " was not a fresh APPLIED receipt (status " +
                    envelope.Receipt.Status + " replayed " + envelope.Receipt.Replayed + ").");
            }
        }

        private static async Task<TableCheckpoint> PersistPostClaimCheckpointAsync(
            Mahjong.TableService.RoomHost host,
            string room,
            ulong claimVersion)
        {
            TableOperationResult saved = await host.Actor.SaveCheckpointAsync().ConfigureAwait(false);
            TableCheckpoint? checkpoint = await host.Store.LoadCheckpointAsync(room, CancellationToken.None)
                .ConfigureAwait(false);
            if (checkpoint == null || checkpoint.StateVersion.Value < claimVersion)
            {
                throw new InvalidOperationException(
                    "No post-claim checkpoint exists (save status " + saved.Status +
                    " reason " + saved.ReasonCode + ").");
            }

            return checkpoint;
        }

        private static async Task<TableCheckpoint> PersistPostPlayCheckpointAsync(
            Mahjong.TableService.RoomHost host,
            string room,
            ulong atLeastVersion)
        {
            TableOperationResult saved = await host.Actor.SaveCheckpointAsync().ConfigureAwait(false);
            TableCheckpoint? checkpoint = await host.Store.LoadCheckpointAsync(room, CancellationToken.None)
                .ConfigureAwait(false);
            if (checkpoint == null || checkpoint.StateVersion.Value < atLeastVersion)
            {
                throw new InvalidOperationException(
                    "No post-play checkpoint exists at/after version " + atLeastVersion +
                    " (save status " + saved.Status + " reason " + saved.ReasonCode + ").");
            }

            return checkpoint;
        }

        private static Dictionary<string, object?> ReadOpeningFact(
            TableCheckpoint checkpoint,
            RoundRecoveryState state,
            int seat,
            int tile)
        {
            if (checkpoint.CommittedHistoryPayload == null || checkpoint.CommittedFactsPayload == null)
            {
                throw new InvalidOperationException("The checkpoint carries no committed history/facts evidence.");
            }

            IReadOnlyList<TableEventRecord> history =
                TableCommittedHistoryCodec.Decode(checkpoint.CommittedHistoryPayload);
            TableEventRecord record = history.FirstOrDefault(row =>
                row.Type == RoundEventType.TileDiscarded &&
                row.Seat == seat &&
                row.Tile.HasValue &&
                row.Tile.Value.Value == tile) ??
                throw new InvalidOperationException("The committed history has no opening discard record.");
            TableProjectionContextData facts = TableProjectionFactsCodec.Decode(
                checkpoint.CommittedFactsPayload,
                state.Spec.RoundId,
                checkpoint.StateVersion);
            TableProjectionEventFact fact = facts.EventFacts.SingleOrDefault(candidate =>
                candidate.EventSeq == record.Seq) ??
                throw new InvalidOperationException("The event facts have no opening discard sequence.");
            if (fact.Type != ProtocolEventType.TileDiscarded ||
                fact.Facts.DiscardTileId != (uint)tile ||
                !fact.Facts.Tsumogiri.HasValue ||
                !fact.Facts.RiichiDiscard.HasValue ||
                !fact.Facts.TurnNumber.HasValue)
            {
                throw new InvalidOperationException("The opening discard event fact is incomplete or mismatched.");
            }

            return new Dictionary<string, object?>
            {
                ["tile"] = (long)fact.Facts.DiscardTileId.Value,
                ["tsumogiri"] = fact.Facts.Tsumogiri.Value,
                ["riichiDiscard"] = fact.Facts.RiichiDiscard.Value,
                ["turnNumber"] = (long)fact.Facts.TurnNumber.Value,
                ["eventSeq"] = (long)record.Seq.Value,
                ["commandVersion"] = (long)record.CommandVersion.Value
            };
        }

        private static void AssertOpeningFact(JsonElement before, Dictionary<string, object?> after)
        {
            if (before.GetProperty("tile").GetInt64() != Convert.ToInt64(after["tile"], CultureInfo.InvariantCulture) ||
                before.GetProperty("tsumogiri").GetBoolean() != Convert.ToBoolean(after["tsumogiri"], CultureInfo.InvariantCulture) ||
                before.GetProperty("riichiDiscard").GetBoolean() != Convert.ToBoolean(after["riichiDiscard"], CultureInfo.InvariantCulture) ||
                before.GetProperty("turnNumber").GetInt64() != Convert.ToInt64(after["turnNumber"], CultureInfo.InvariantCulture) ||
                before.GetProperty("eventSeq").GetInt64() != Convert.ToInt64(after["eventSeq"], CultureInfo.InvariantCulture) ||
                before.GetProperty("commandVersion").GetInt64() != Convert.ToInt64(after["commandVersion"], CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException("The persisted opening discard fact changed across recovery.");
            }
        }

        private static Dictionary<string, object?> CaptureHistory(PlayerSnapshot snapshot)
        {
            WirePublicBoardView board = snapshot.PublicView!.Board;
            return new Dictionary<string, object?>
            {
                ["board"] = BoardFingerprint(board),
                ["seats"] = board.Players
                    .OrderBy(player => player.Seat)
                    .Select(player => new Dictionary<string, object?>
                    {
                        ["seat"] = player.Seat,
                        ["riichi"] = player.Riichi,
                        ["melds"] = player.Melds.Select(MeldKey).ToList(),
                        ["discards"] = player.Discards.Select(DiscardKey).ToList()
                    })
                    .ToList()
            };
        }

        private static string BoardFingerprint(WirePublicBoardView board)
        {
            return "wind" + board.RoundWind.ToString(CultureInfo.InvariantCulture) +
                   " number" + board.RoundNumber.ToString(CultureInfo.InvariantCulture) +
                   " honba" + board.Honba.ToString(CultureInfo.InvariantCulture) +
                   " dealer" + board.DealerSeat.ToString(CultureInfo.InvariantCulture) +
                   " dora[" + string.Join(",", board.DoraIndicators.Select(value =>
                       value.ToString(CultureInfo.InvariantCulture))) + "]" +
                   " scores[" + string.Join(";", board.Scores.Select(score =>
                       score.Seat.ToString(CultureInfo.InvariantCulture) + "=" +
                       score.Score.ToString(CultureInfo.InvariantCulture))) + "]";
        }

        private static string MeldKey(WireMeldView meld)
        {
            return ((int)meld.Type).ToString(CultureInfo.InvariantCulture) + ":" +
                   string.Join("+", meld.TileIds.Select(value => value.ToString(CultureInfo.InvariantCulture)));
        }

        private static string DiscardKey(WireDiscardView discard)
        {
            return discard.TileId.ToString(CultureInfo.InvariantCulture) + ":" +
                   discard.Tsumogiri + ":" + discard.RiichiDiscard + ":" +
                   discard.TurnNumber.ToString(CultureInfo.InvariantCulture);
        }

        private static void AssertHistoryExtends(JsonElement before, PlayerSnapshot afterSnapshot)
        {
            Dictionary<string, object?> after = CaptureHistory(afterSnapshot);
            string beforeBoard = before.GetProperty("board").GetString()!;
            string afterBoard = (string)after["board"]!;
            if (!string.Equals(beforeBoard, afterBoard, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The recovered immutable board facts changed across the crash.");
            }

            List<Dictionary<string, object?>> afterSeats =
                (List<Dictionary<string, object?>>)after["seats"]!;
            foreach (JsonElement beforeSeat in before.GetProperty("seats").EnumerateArray())
            {
                int seat = beforeSeat.GetProperty("seat").GetInt32();
                Dictionary<string, object?> afterSeat = afterSeats.Single(item => (int)item["seat"]! == seat);
                bool beforeRiichi = beforeSeat.GetProperty("riichi").GetBoolean();
                bool afterRiichi = (bool)afterSeat["riichi"]!;
                if (beforeRiichi && !afterRiichi)
                {
                    throw new InvalidOperationException("Seat " + seat + " lost its riichi register across the crash.");
                }

                AssertPrefix(beforeSeat.GetProperty("melds"), (List<string>)afterSeat["melds"]!, "seat " + seat + " melds");
                AssertPrefix(beforeSeat.GetProperty("discards"), (List<string>)afterSeat["discards"]!, "seat " + seat + " discards");
            }
        }

        private static void AssertPrefix(JsonElement before, List<string> after, string what)
        {
            List<string> expected = before.EnumerateArray().Select(item => item.GetString()!).ToList();
            if (after.Count < expected.Count || !expected.SequenceEqual(after.Take(expected.Count)))
            {
                throw new InvalidOperationException("The recovered " + what + " was rewritten across the crash.");
            }
        }

        private static async Task<Envelope> PlayOneAuthoritativeDiscardAsync(
            Mahjong.TableService.TableServiceHostRuntime runtime,
            WorkerTicketSigner signer,
            string room,
            TableTestClient observer,
            ulong minVersion,
            Dictionary<int, TableTestClient> seatClients,
            List<TableTestClient> created,
            Dictionary<string, string> connectionOptions)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                WirePublicBoardView board = await WaitForActionableBoardAsync(observer, minVersion).ConfigureAwait(false);
                int seat = board.CurrentSeat;
                if (!seatClients.TryGetValue(seat, out TableTestClient? actor))
                {
                    string url = "wss://127.0.0.1:" +
                                 runtime.GetBoundPort().ToString(CultureInfo.InvariantCulture) +
                                 "/v1/table?room=" + Uri.EscapeDataString(room);
                    actor = await Program.ConnectTableClientAsync(connectionOptions, url).ConfigureAwait(false);
                    _ = await actor.AdmitAsync(signer.SignTicket("claimed-recover-" + seat, room, seat))
                        .ConfigureAwait(false);
                    seatClients[seat] = actor;
                    created.Add(actor);
                }

                Envelope decisionEnvelope = await actor.ReadUntilAsync(
                    message => message.PlayerUpdate?.Snapshot is PlayerSnapshot snapshot &&
                               snapshot.PrivateView?.Decision != null &&
                               snapshot.PublicView?.Board.CurrentSeat == seat &&
                               snapshot.StateVersion >= minVersion,
                    TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                PlayerSnapshot snapshot = decisionEnvelope.PlayerUpdate!.Snapshot;
                WireDecisionView decision = snapshot.PrivateView!.Decision!;
                WireLegalActionView? discard = decision.Actions.FirstOrDefault(candidate =>
                    candidate.ActionType == (uint)ProtocolActionType.DiscardTile);
                if (discard == null)
                {
                    minVersion = snapshot.StateVersion;
                    continue;
                }

                string request = TableWire.RandomId("req");
                await actor.SendAsync(TableWire.Command(
                    actor.Welcome!,
                    request,
                    CommandKind.SubmitAction,
                    TableWire.SubmitActionPayload(
                        snapshot.RoundId,
                        decision.DecisionId,
                        decision.ControlEpoch,
                        discard.ActionType,
                        discard.TileIds.ToArray()),
                    expectedStateVersion: snapshot.StateVersion)).ConfigureAwait(false);
                Envelope receipt = await actor.ReadUntilAsync(
                    candidate => candidate.MessageType == MessageType.Receipt &&
                                 candidate.RequestId == request,
                    TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                if (receipt.Receipt.Status == WireReceiptStatus.Applied)
                {
                    return receipt;
                }

                minVersion = snapshot.StateVersion;
            }

            throw new InvalidOperationException("No authoritative post-recovery discard could be applied.");
        }

        private static async Task<WirePublicBoardView> WaitForActionableBoardAsync(
            TableTestClient observer,
            ulong minVersion)
        {
            Envelope envelope = await observer.ReadUntilAsync(
                message => message.PlayerUpdate?.Snapshot is PlayerSnapshot snapshot &&
                           snapshot.StateVersion >= minVersion &&
                           snapshot.PublicView?.Board is WirePublicBoardView board &&
                           (board.Phase == GamePhase.CheatWindow || board.Phase == GamePhase.Discard) &&
                           board.CurrentSeat >= 0 &&
                           board.CurrentSeat <= 3,
                TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            return envelope.PlayerUpdate!.Snapshot.PublicView!.Board;
        }

        private static int CountAllTiles(TilePositionMap tiles)
        {
            int total = tiles.CountAt(TileZone.Wall) +
                        tiles.CountAt(TileZone.DeadWall) +
                        tiles.CountAt(TileZone.DeadWallReplacement);
            for (int seat = 0; seat < 4; seat++)
            {
                total += tiles.CountAt(TileZone.Hand, seat);
                total += tiles.CountAt(TileZone.Discard, seat);
                total += tiles.CountAt(TileZone.Meld, seat);
                total += tiles.CountAt(TileZone.Sleeve, seat);
            }

            return total;
        }

        private static Task<int> AppliedReceiptCountAsync(string room)
        {
            return Program.ScalarCountAsync(
                Program.RequireRuntimeConnection(),
                "SELECT count(*) FROM table_store.request_receipt WHERE room_id = @room_id AND status = 'APPLIED'",
                room);
        }
    }
}
