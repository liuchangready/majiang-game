using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.P5_4b.B1.HttpsAdmission.Worker;
using WireDecisionView = Mahjong.Contracts.Wire.DecisionView;
using WireLegalActionView = Mahjong.Contracts.Wire.LegalActionView;
using WirePublicMatchView = Mahjong.Contracts.Wire.PublicMatchView;
using CoreReceiptStatus = Mahjong.Contracts.ReceiptStatus;

namespace Mahjong.P5_4b.B2.Terminal.Worker
{
    /// <summary>
    /// P5.4b-B2-T formal worker: real HTTPS Lobby ticket issuance -> real WSS TableService
    /// with four players and an independent spectator, a natural wall exhaustion, and a
    /// post-terminal restart observation. It never fabricates a terminal or a settlement
    /// body; the only terminal it records is the one the authoritative service commits.
    /// </summary>
    public static class Program
    {
        public const int ExitOk = 0;
        public const int ExitFailed = 1;
        public const int ExitBlocked = 2;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static async Task<int> Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    Console.Error.WriteLine("usage: natural|verify --lobby-url ... --table-port ... --ca-root ... --out ...");
                    return ExitBlocked;
                }

                string mode = args[0];
                if (string.Equals(mode, "gate-selftest", StringComparison.Ordinal))
                {
                    return RunGateSelfTest();
                }

                Options options = Options.Parse(args.Skip(1).ToArray());
                if (string.Equals(mode, "health", StringComparison.Ordinal))
                {
                    return await RunHealthAsync(options).ConfigureAwait(false);
                }

                if (string.Equals(mode, "natural", StringComparison.Ordinal))
                {
                    return await RunNaturalAsync(options).ConfigureAwait(false);
                }

                if (string.Equals(mode, "verify", StringComparison.Ordinal))
                {
                    return await RunVerifyAsync(options).ConfigureAwait(false);
                }

                Console.Error.WriteLine("unknown mode: " + mode);
                return ExitBlocked;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("b2-terminal-worker failed: " + exception.GetType().Name + ": " +
                                        Redact(exception.Message));
                return ExitFailed;
            }
        }

        private static async Task<int> RunHealthAsync(Options options)
        {
            string url = options.Require("url");
            string caRootPath = options.Require("ca-root");
            string expect = options.Require("expect");
            using var root = new X509Certificate2(caRootPath);
            using var client = new B1LobbyClient(new Uri(url), root);
            B1LobbyClient.LobbyHttpResult result = await client
                .GetAsync(string.Empty, CancellationToken.None)
                .ConfigureAwait(false);
            if (!string.Equals(
                    result.StatusCode.ToString(CultureInfo.InvariantCulture),
                    expect,
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine("health expected " + expect + " but observed " + result.StatusCode + ".");
                return ExitFailed;
            }

            Console.WriteLine("B2-HEALTH-OK status=" + result.StatusCode);
            return ExitOk;
        }

        private static async Task<int> RunNaturalAsync(Options options)
        {
            string lobbyUrl = options.Require("lobby-url");
            int tablePort = int.Parse(options.Require("table-port"), CultureInfo.InvariantCulture);
            string caRootPath = options.Require("ca-root");
            string outputPath = options.Require("out");
            string secretsDirectory = options.Require("secrets");
            string outboxRoot = options.Require("outbox-root");
            int durationSeconds = options.OptionalInt("duration-seconds", 240);
            Directory.CreateDirectory(secretsDirectory);
            Directory.CreateDirectory(outboxRoot);

            using var root = new X509Certificate2(caRootPath);
            using var lobby = new B1LobbyClient(new Uri(lobbyUrl), root);
            var evidence = new Dictionary<string, object?>(StringComparer.Ordinal);
            var guests = new List<GuestState>();
            var players = new List<ClientHandle>();
            ClientHandle? spectator = null;
            string roomId = string.Empty;
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    GuestState guest = await B1Flows.CreateGuestAsync(
                        lobby, "b2-guest-" + i, CancellationToken.None).ConfigureAwait(false);
                    guests.Add(guest);
                }

                GuestState host = guests[0];
                (roomId, string hostAssignment, int hostSeat) = await B1Flows.CreateRoomAsync(
                    lobby, host, "B2 terminal " + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    "b2-create-" + Guid.NewGuid().ToString("N"), CancellationToken.None).ConfigureAwait(false);
                evidence["roomId"] = roomId;
                evidence["hostSeat"] = hostSeat;
                if (hostSeat != 0 || string.IsNullOrEmpty(hostAssignment))
                {
                    throw new InvalidOperationException("The host did not receive seat 0.");
                }

                var seats = new int[4];
                seats[0] = hostSeat;
                for (int i = 1; i < 4; i++)
                {
                    (string assignment, int seat) = await B1Flows.JoinRoomAsync(
                        lobby, guests[i], roomId, i,
                        "b2-join-" + i + "-" + Guid.NewGuid().ToString("N"), CancellationToken.None)
                        .ConfigureAwait(false);
                    seats[i] = seat;
                    if (seat != i)
                    {
                        throw new InvalidOperationException("Join did not receive the requested seat " + i + ".");
                    }

                    if (string.IsNullOrEmpty(assignment))
                    {
                        throw new InvalidOperationException("Join returned an empty assignment.");
                    }
                }

                await B1Flows.SpectateAsync(
                    lobby, guests[4], roomId,
                    "b2-spec-" + Guid.NewGuid().ToString("N"), CancellationToken.None).ConfigureAwait(false);

                var tickets = new List<B1Flows.IssuedTicket>();
                for (int i = 0; i < 5; i++)
                {
                    B1Flows.IssuedTicket ticket = await B1Flows.IssueTicketAsync(
                        lobby, guests[i], "b2-ticket-" + i + "-" + Guid.NewGuid().ToString("N"),
                        CancellationToken.None).ConfigureAwait(false);
                    if (!string.Equals(ticket.RoomId, roomId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("A Lobby ticket belongs to another room.");
                    }

                    tickets.Add(ticket);
                }

                Uri endpoint = new Uri("wss://127.0.0.1:" + tablePort + "/v1/table?room=" +
                                       Uri.EscapeDataString(roomId));
                for (int i = 0; i < 4; i++)
                {
                    var handle = new ClientHandle(
                        endpoint, root, Path.Combine(outboxRoot, "player-" + i))
                    {
                        Role = "PLAYER",
                        AccountId = guests[i].AccountId,
                        AssignmentId = guests[i].AssignmentId ?? string.Empty,
                        Seat = seats[i]
                    };
                    await handle.OpenAsync(tickets[i].Ticket, TimeSpan.FromSeconds(20), CancellationToken.None)
                        .ConfigureAwait(false);
                    players.Add(handle);
                    await handle.WaitForAsync(
                        () => handle.LatestPlayer?.PrivateView != null,
                        TimeSpan.FromSeconds(20),
                        "player " + i + " full private baseline").ConfigureAwait(false);
                }

                spectator = new ClientHandle(
                    endpoint, root, Path.Combine(outboxRoot, "spectator"))
                {
                    Role = "SPECTATOR",
                    AccountId = guests[4].AccountId,
                    AssignmentId = guests[4].AssignmentId ?? string.Empty,
                    Seat = -1
                };
                await spectator.OpenAsync(tickets[4].Ticket, TimeSpan.FromSeconds(20), CancellationToken.None)
                    .ConfigureAwait(false);
                await spectator.WaitForAsync(
                    () => spectator.LatestPublic != null,
                    TimeSpan.FromSeconds(20),
                    "spectator public baseline").ConfigureAwait(false);

                evidence["admission"] = new
                {
                    guests = guests.Count,
                    players = players.Count,
                    spectator = true,
                    seats,
                    distinctSessions = players.Select(p => p.Binding!.SessionId.Value)
                        .Append(spectator.Binding!.SessionId.Value).Distinct().Count(),
                    spectatorSeat = spectator.Seat
                };

                var seen = new HashSet<string>(StringComparer.Ordinal);
                int actions = 0;
                int discards = 0;
                int passes = 0;
                int staleRejections = 0;
                int cheatOfferSkips = 0;
                string firstFailure = string.Empty;
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(durationSeconds);
                PublicSnapshot? terminal = null;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    PublicSnapshot? publicNow = spectator.LatestPublic;
                    if (publicNow?.View?.Board?.Phase == GamePhase.RoundEnd)
                    {
                        terminal = publicNow;
                        break;
                    }

                    bool worked = false;
                    for (int i = 0; i < players.Count; i++)
                    {
                        ClientHandle handle = players[i];
                        PlayerSnapshot? snapshot = handle.LatestPlayer;
                        WireDecisionView? decision = snapshot?.PrivateView?.Decision;
                        if (snapshot == null || decision == null || !seen.Add(decision.DecisionId))
                        {
                            continue;
                        }

                        WireLegalActionView? discard = B1Actions.FindDiscard(decision);
                        WireLegalActionView? pass = B1Actions.FindPass(decision);
                        WireLegalActionView? selected = discard ?? pass;
                        if (selected == null)
                        {
                            continue;
                        }

                        string requestId = "b2-act-" + Guid.NewGuid().ToString("N");
                        ProtocolCommand command = B1Actions.BuildCommand(handle, snapshot, decision, selected, requestId);
                        TableReceiptResolvedEventArgs receipt;
                        try
                        {
                            receipt = await B1Actions.SubmitAndWaitAsync(
                                handle, command, TimeSpan.FromSeconds(20), CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            firstFailure = "receipt: " + exception.GetType().Name + ": " + Redact(exception.Message);
                            evidence["firstFailure"] = firstFailure;
                            break;
                        }

                        if (!receipt.Resolution.Succeeded ||
                            receipt.Resolution.ReceiptStatus != CoreReceiptStatus.Applied)
                        {
                            string reason = receipt.Resolution.ReasonCode ?? string.Empty;
                            // A stale response-window decision is a definitive zero-side-effect
                            // rejection. The next tick/commit publishes a fresh decision; this
                            // worker skips the old one and never retries an unknown result.
                            if (reason.IndexOf("STALE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                reason.IndexOf("DECISION_MISMATCH", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                reason.IndexOf("DEADLINE", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                staleRejections++;
                                await Task.Delay(50).ConfigureAwait(false);
                                continue;
                            }

                            // An optional cheat offer can be open on the same turn. This
                            // natural driver deliberately does not exercise cheat commands;
                            // it declines by waiting for the offer deadline, then consumes the
                            // next authoritative decision. The rejected request is not retried.
                            if (string.Equals(reason, "CHEAT_RESERVATION_MISSING", StringComparison.Ordinal))
                            {
                                cheatOfferSkips++;
                                await Task.Delay(100).ConfigureAwait(false);
                                continue;
                            }

                            firstFailure = "receipt_not_applied seat=" + i + " status=" +
                                           receipt.Resolution.ReceiptStatus + " reason=" + reason;
                            evidence["firstFailure"] = firstFailure;
                            break;
                        }

                        actions++;
                        if (discard != null)
                        {
                            discards++;
                        }
                        else
                        {
                            passes++;
                        }

                        worked = true;
                    }

                    if (!string.IsNullOrEmpty(firstFailure))
                    {
                        break;
                    }

                    if (!worked)
                    {
                        await Task.Delay(50).ConfigureAwait(false);
                    }
                }

                evidence["actions"] = actions;
                evidence["discards"] = discards;
                evidence["passes"] = passes;
                evidence["staleRejections"] = staleRejections;
                evidence["cheatOfferSkips"] = cheatOfferSkips;
                evidence["terminalReached"] = terminal != null;
                bool? postUnchanged = null;
                bool? postNoRepublish = null;
                if (terminal != null)
                {
                    WirePublicMatchView view = terminal.View;
                    evidence["terminal"] = new
                    {
                        stateVersion = terminal.StateVersion,
                        roundId = terminal.RoundId,
                        phase = view.Board.Phase.ToString(),
                        wallRemaining = view.Board.WallRemaining,
                        settlementRoundId = view.SettlementRoundId,
                        settlementEventTypes = view.SettlementEvents.Select(e => e.EventType.ToString()).ToArray(),
                        finalScores = view.Board.Scores.Select(s => s.Score).ToArray(),
                        sessionFinished = view.Session?.Finished ?? false,
                        spectatorPrivacyViolated = spectator.PrivacyViolated
                    };

                    // Multiple authoritative ticks after the terminal must not change the
                    // terminal version or add another settlement event.
                    ulong terminalVersion = terminal.StateVersion;
                    int terminalEvents = view.SettlementEvents.Count;
                    await Task.Delay(1500).ConfigureAwait(false);
                    ClientHandle observer = spectator!;
                    await observer.WaitForAsync(
                        () => observer.LatestPublic?.StateVersion == terminalVersion,
                        TimeSpan.FromSeconds(5),
                        "post-terminal stable version").ConfigureAwait(false);
                    PublicSnapshot? later = observer.LatestPublic;
                    postUnchanged = later != null && later.StateVersion == terminalVersion;
                    postNoRepublish = later != null &&
                                      later.StateVersion == terminalVersion &&
                                      later.View.SettlementEvents.Count == terminalEvents;
                    evidence["postTerminal"] = new
                    {
                        stateVersion = later?.StateVersion ?? 0UL,
                        unchanged = postUnchanged,
                        settlementEvents = later?.View?.SettlementEvents.Count ?? -1,
                        noRepublish = postNoRepublish
                    };
                }

                bool playersPrivacyViolated = players.Any(p => p.PrivacyViolated);
                evidence["playersPrivacyViolated"] = playersPrivacyViolated;
                evidence["spectatorPrivacyViolated"] = spectator?.PrivacyViolated ?? false;
                if (terminal == null)
                {
                    WriteJson(outputPath, evidence);
                    Console.WriteLine("B2-TERMINAL-NATURAL BLOCKED room=" + roomId +
                                      " actions=" + actions);
                    return ExitBlocked;
                }

                bool ok = EvaluateNaturalSuccess(
                    terminalReached: true,
                    spectatorPrivacyViolated: spectator!.PrivacyViolated,
                    playersPrivacyViolated: playersPrivacyViolated,
                    postTerminalUnchanged: postUnchanged,
                    postTerminalNoRepublish: postNoRepublish,
                    out string gateFailure);
                evidence["gate"] = new { ok, failure = gateFailure };
                WriteJson(outputPath, evidence);
                Console.WriteLine("B2-TERMINAL-NATURAL " + (ok ? "OK" : "FAIL") +
                                  " room=" + roomId + " actions=" + actions +
                                  " gate=" + gateFailure);
                return ok ? ExitOk : ExitFailed;
            }
            catch (Exception exception)
            {
                evidence["error"] = exception.GetType().Name + ": " + Redact(exception.Message);
                WriteJson(outputPath, evidence);
                Console.Error.WriteLine("B2-TERMINAL-NATURAL-FAIL " + exception.GetType().Name + ": " +
                                        Redact(exception.Message));
                return ExitFailed;
            }
            finally
            {
                if (spectator != null)
                {
                    await spectator.DisposeAsync().ConfigureAwait(false);
                }

                foreach (ClientHandle player in players)
                {
                    await player.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task<int> RunVerifyAsync(Options options)
        {
            string lobbyUrl = options.Require("lobby-url");
            int tablePort = int.Parse(options.Require("table-port"), CultureInfo.InvariantCulture);
            string caRootPath = options.Require("ca-root");
            string outputPath = options.Require("out");
            string roomId = options.Require("room");
            ulong expectedVersion = ulong.Parse(options.Require("state-version"), CultureInfo.InvariantCulture);
            int expectedSettlementEvents = options.OptionalInt("settlement-events", -1);
            string outboxRoot = options.Require("outbox-root");
            Directory.CreateDirectory(outboxRoot);

            using var root = new X509Certificate2(caRootPath);
            using var lobby = new B1LobbyClient(new Uri(lobbyUrl), root);
            var evidence = new Dictionary<string, object?>(StringComparer.Ordinal);
            GuestState guest = await B1Flows.CreateGuestAsync(
                lobby, "b2-verify-guest", CancellationToken.None).ConfigureAwait(false);
            await B1Flows.SpectateAsync(
                lobby, guest, roomId,
                "b2-verify-spec-" + Guid.NewGuid().ToString("N"), CancellationToken.None).ConfigureAwait(false);
            B1Flows.IssuedTicket ticket = await B1Flows.IssueTicketAsync(
                lobby, guest, "b2-verify-ticket-" + Guid.NewGuid().ToString("N"), CancellationToken.None)
                .ConfigureAwait(false);
            Uri endpoint = new Uri("wss://127.0.0.1:" + tablePort + "/v1/table?room=" +
                                   Uri.EscapeDataString(roomId));
            await using var spectator = new ClientHandle(
                endpoint, root, Path.Combine(outboxRoot, "verify-spectator"))
            {
                Role = "SPECTATOR",
                AccountId = guest.AccountId,
                AssignmentId = guest.AssignmentId ?? string.Empty,
                Seat = -1
            };
            await spectator.OpenAsync(ticket.Ticket, TimeSpan.FromSeconds(20), CancellationToken.None)
                .ConfigureAwait(false);
            await spectator.WaitForAsync(
                () => spectator.LatestPublic != null,
                TimeSpan.FromSeconds(20),
                "post-restart public baseline").ConfigureAwait(false);
            PublicSnapshot snapshot = spectator.LatestPublic!;
            evidence["roomId"] = roomId;
            evidence["stateVersion"] = snapshot.StateVersion;
            evidence["phase"] = snapshot.View.Board.Phase.ToString();
            evidence["wallRemaining"] = snapshot.View.Board.WallRemaining;
            evidence["settlementRoundId"] = snapshot.View.SettlementRoundId;
            evidence["settlementEvents"] = snapshot.View.SettlementEvents.Count;
            evidence["sessionFinished"] = snapshot.View.Session?.Finished ?? false;
            evidence["spectatorPrivacyViolated"] = spectator.PrivacyViolated;
            bool ok = EvaluateVerifySuccess(
                phaseRoundEnd: snapshot.View.Board.Phase == GamePhase.RoundEnd,
                versionMatch: snapshot.StateVersion == expectedVersion,
                settlementEventCountMatch: expectedSettlementEvents < 0 ||
                                           snapshot.View.SettlementEvents.Count == expectedSettlementEvents,
                spectatorPrivacyViolated: spectator.PrivacyViolated,
                out string gateFailure);
            evidence["gate"] = new { ok, failure = gateFailure };
            WriteJson(outputPath, evidence);
            Console.WriteLine("B2-TERMINAL-VERIFY " + (ok ? "OK" : "FAIL") +
                              " room=" + roomId + " version=" + snapshot.StateVersion +
                              " gate=" + gateFailure);
            return ok ? ExitOk : ExitFailed;
        }

        /// <summary>
        /// The actual natural-run success gate. Null/absent evidence is a failure, not a
        /// default success; privacy violations and post-terminal re-publication are blocking.
        /// </summary>
        public static bool EvaluateNaturalSuccess(
            bool terminalReached,
            bool? spectatorPrivacyViolated,
            bool? playersPrivacyViolated,
            bool? postTerminalUnchanged,
            bool? postTerminalNoRepublish,
            out string failure)
        {
            if (!terminalReached)
            {
                failure = "terminal-missing";
                return false;
            }

            if (spectatorPrivacyViolated != false)
            {
                failure = "spectator-privacy";
                return false;
            }

            if (playersPrivacyViolated != false)
            {
                failure = "player-privacy";
                return false;
            }

            if (postTerminalUnchanged != true)
            {
                failure = "terminal-version-changed-or-missing";
                return false;
            }

            if (postTerminalNoRepublish != true)
            {
                failure = "terminal-republished-or-missing";
                return false;
            }

            failure = string.Empty;
            return true;
        }

        /// <summary>Actual post-restart success gate; privacy evidence is mandatory.</summary>
        public static bool EvaluateVerifySuccess(
            bool phaseRoundEnd,
            bool versionMatch,
            bool settlementEventCountMatch,
            bool? spectatorPrivacyViolated,
            out string failure)
        {
            if (!phaseRoundEnd)
            {
                failure = "phase-not-round-end";
                return false;
            }

            if (!versionMatch)
            {
                failure = "terminal-version-mismatch";
                return false;
            }

            if (!settlementEventCountMatch)
            {
                failure = "settlement-event-count-mismatch";
                return false;
            }

            if (spectatorPrivacyViolated != false)
            {
                failure = "spectator-privacy";
                return false;
            }

            failure = string.Empty;
            return true;
        }

        private static int RunGateSelfTest()
        {
            int failed = 0;
            void Check(string id, bool actual, bool expected)
            {
                bool ok = actual == expected;
                Console.WriteLine("GATE-SELFTEST " + id + ": actual=" + actual +
                                  " expected=" + expected + " " + (ok ? "PASS" : "FAIL"));
                if (!ok)
                {
                    failed++;
                }
            }

            Check("natural-good", EvaluateNaturalSuccess(
                true, false, false, true, true, out _), true);
            Check("natural-spectator-privacy", EvaluateNaturalSuccess(
                true, true, false, true, true, out _), false);
            Check("natural-player-privacy", EvaluateNaturalSuccess(
                true, false, true, true, true, out _), false);
            Check("natural-republish", EvaluateNaturalSuccess(
                true, false, false, true, false, out _), false);
            Check("natural-version-changed", EvaluateNaturalSuccess(
                true, false, false, false, true, out _), false);
            Check("natural-missing-privacy", EvaluateNaturalSuccess(
                true, null, false, true, true, out _), false);
            Check("natural-missing-republish", EvaluateNaturalSuccess(
                true, false, false, true, null, out _), false);
            Check("natural-terminal-missing", EvaluateNaturalSuccess(
                false, false, false, true, true, out _), false);

            Check("verify-good", EvaluateVerifySuccess(true, true, true, false, out _), true);
            Check("verify-privacy", EvaluateVerifySuccess(true, true, true, true, out _), false);
            Check("verify-missing-privacy", EvaluateVerifySuccess(true, true, true, null, out _), false);
            Check("verify-version", EvaluateVerifySuccess(true, false, true, false, out _), false);
            Check("verify-events", EvaluateVerifySuccess(true, true, false, false, out _), false);
            Check("verify-phase", EvaluateVerifySuccess(false, true, true, false, out _), false);
            Console.WriteLine("GATE-SELFTEST SUMMARY: failures=" + failed);
            return failed == 0 ? ExitOk : ExitFailed;
        }

        public static string Redact(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            string compact = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return compact.Length > 200 ? compact.Substring(0, 200) : compact;
        }

        public static void WriteJson(string path, object value)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temp, path);
        }

        private sealed class Options
        {
            private readonly Dictionary<string, string> _values =
                new Dictionary<string, string>(StringComparer.Ordinal);

            public static Options Parse(string[] args)
            {
                var options = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Expected --key value, got " + args[i] + ".");
                    }

                    options._values[args[i].Substring(2)] = args[i + 1];
                    i++;
                }

                return options;
            }

            public string Require(string name)
            {
                if (!_values.TryGetValue(name, out string? value) || string.IsNullOrEmpty(value))
                {
                    throw new ArgumentException("Missing required --" + name + ".");
                }

                return value;
            }

            public int OptionalInt(string name, int fallback)
            {
                return _values.TryGetValue(name, out string? value)
                    ? int.Parse(value, CultureInfo.InvariantCulture)
                    : fallback;
            }
        }
    }
}
