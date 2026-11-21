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
            bool includeSpectator = options.OptionalBool("spectator", true);
            bool requireLobbyTerminal = options.OptionalBool("require-lobby-terminal", false);
            bool exerciseExpiredAssignment = options.OptionalBool("exercise-expired-assignment", false);
            bool exerciseCapacityReuse = options.OptionalBool("exercise-capacity-reuse", false);
            bool exercisePublicReads = options.OptionalBool("exercise-public-reads", false);
            int actionDelayMs = options.OptionalInt("action-delay-ms", 0);
            int renewAtSeconds = options.OptionalInt("renew-at-seconds", 0);
            Directory.CreateDirectory(secretsDirectory);
            Directory.CreateDirectory(outboxRoot);

            using var root = new X509Certificate2(caRootPath);
            using var lobby = new B1LobbyClient(new Uri(lobbyUrl), root);
            var evidence = new Dictionary<string, object?>(StringComparer.Ordinal);
            var guests = new List<GuestState>();
            var players = new List<ClientHandle>();
            using var publicReadLifetime = new CancellationTokenSource();
            Task publicReadTask = Task.CompletedTask;
            int publicReadCount = 0;
            int publicReadFailures = 0;
            ClientHandle? spectator = null;
            string roomId = string.Empty;
            try
            {
                int requiredGuests = (includeSpectator ? 5 : 4) + (exerciseExpiredAssignment ? 2 : 0);
                for (int i = 0; i < requiredGuests; i++)
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
                var playerGuests = new List<GuestState>(4);
                string? expiredAssignmentTicket = null;
                if (exerciseExpiredAssignment)
                {
                    (string expiredAssignment, int expiredSeat) = await B1Flows.JoinRoomAsync(
                        lobby, guests[1], roomId, 1,
                        "b2-expiring-seat-" + Guid.NewGuid().ToString("N"), CancellationToken.None)
                        .ConfigureAwait(false);
                    if (expiredSeat != 1 || string.IsNullOrEmpty(expiredAssignment))
                    {
                        throw new InvalidOperationException("The temporary seat-1 reservation was not created.");
                    }

                    expiredAssignmentTicket = (await B1Flows.IssueTicketAsync(
                        lobby, guests[1], "b2-expiring-ticket-" + Guid.NewGuid().ToString("N"), CancellationToken.None)
                        .ConfigureAwait(false)).Ticket;
                    await Task.Delay(TimeSpan.FromSeconds(options.OptionalInt("reservation-expiry-wait-seconds", 16)))
                        .ConfigureAwait(false);
                    (string replacementAssignment, int replacementSeat) = await B1Flows.JoinRoomAsync(
                        lobby, guests[2], roomId, 1,
                        "b2-replacement-seat-" + Guid.NewGuid().ToString("N"), CancellationToken.None)
                        .ConfigureAwait(false);
                    seats[0] = hostSeat;
                    seats[1] = replacementSeat;
                    if (replacementSeat != 1 || string.IsNullOrEmpty(replacementAssignment))
                    {
                        throw new InvalidOperationException("Expired temporary seat reservation was not reassigned to seat 1.");
                    }

                    playerGuests.Add(host);
                    playerGuests.Add(guests[2]);
                    for (int seat = 1; seat < 4; seat++)
                    {
                        if (seat == 1)
                        {
                            continue;
                        }

                        GuestState joining = guests[seat + 1];
                        (string assignment, int joinedSeat) = await B1Flows.JoinRoomAsync(
                            lobby, joining, roomId, seat,
                            "b2-join-" + seat + "-" + Guid.NewGuid().ToString("N"), CancellationToken.None)
                            .ConfigureAwait(false);
                        if (joinedSeat != seat || string.IsNullOrEmpty(assignment))
                        {
                            throw new InvalidOperationException("Join did not receive unique seat " + seat + ".");
                        }

                        seats[seat] = joinedSeat;
                        playerGuests.Add(joining);
                    }
                }
                else
                {
                    seats[0] = hostSeat;
                    playerGuests.Add(host);
                    for (int i = 1; i < 4; i++)
                    {
                        (string assignment, int seat) = await B1Flows.JoinRoomAsync(
                            lobby, guests[i], roomId, i,
                            "b2-join-" + i + "-" + Guid.NewGuid().ToString("N"), CancellationToken.None)
                            .ConfigureAwait(false);
                        seats[i] = seat;
                        if (seat != i || string.IsNullOrEmpty(assignment))
                        {
                            throw new InvalidOperationException("Join did not receive unique seat " + i + ".");
                        }

                        playerGuests.Add(guests[i]);
                    }
                }

                Uri endpoint = new Uri("wss://127.0.0.1:" + tablePort + "/v1/table?room=" +
                                       Uri.EscapeDataString(roomId));
                if (expiredAssignmentTicket != null)
                {
                    string? staleAssignmentCode = await ProbeAdmissionRejectionAsync(
                        endpoint, root, expiredAssignmentTicket).ConfigureAwait(false);
                    bool staleAssignmentRejected = IsAdmissionDenied(staleAssignmentCode);
                    evidence["expiredAssignmentTicket"] = new { staleAssignmentCode, rejected = staleAssignmentRejected };
                    if (!staleAssignmentRejected)
                    {
                        throw new InvalidOperationException("The table admitted a ticket whose Lobby assignment had expired and been replaced.");
                    }
                }

                if (includeSpectator)
                {
                    await B1Flows.SpectateAsync(
                        lobby, guests[exerciseExpiredAssignment ? 5 : 4], roomId,
                        "b2-spec-" + Guid.NewGuid().ToString("N"), CancellationToken.None).ConfigureAwait(false);
                }

                var tickets = new List<B1Flows.IssuedTicket>();
                var admittedGuests = new List<GuestState>(playerGuests);
                if (includeSpectator)
                {
                    admittedGuests.Add(guests[exerciseExpiredAssignment ? 5 : 4]);
                }

                for (int i = 0; i < admittedGuests.Count; i++)
                {
                    B1Flows.IssuedTicket ticket = await B1Flows.IssueTicketAsync(
                        lobby, admittedGuests[i], "b2-ticket-" + i + "-" + Guid.NewGuid().ToString("N"),
                        CancellationToken.None).ConfigureAwait(false);
                    if (!string.Equals(ticket.RoomId, roomId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("A Lobby ticket belongs to another room.");
                    }

                    tickets.Add(ticket);
                }

                string invalidTicket = TamperSignature(tickets[0].Ticket);
                string? invalidTicketCode = await ProbeAdmissionRejectionAsync(
                    endpoint, root, invalidTicket).ConfigureAwait(false);
                Uri foreignRoomEndpoint = new Uri(
                    "wss://127.0.0.1:" + tablePort + "/v1/table?room=" +
                    Uri.EscapeDataString(roomId + "-foreign"));
                string? foreignRoomCode = await ProbeAdmissionRejectionAsync(
                    foreignRoomEndpoint, root, tickets[0].Ticket).ConfigureAwait(false);
                bool invalidTicketRejected = IsAdmissionDenied(invalidTicketCode);
                bool foreignRoomTicketRejected = IsAdmissionDenied(foreignRoomCode);
                evidence["admissionNegatives"] = new
                {
                    invalidTicketCode,
                    invalidTicketRejected,
                    foreignRoomCode,
                    foreignRoomTicketRejected
                };
                if (!invalidTicketRejected || !foreignRoomTicketRejected)
                {
                    throw new InvalidOperationException("The published table host admitted an invalid or cross-room ticket.");
                }

                for (int i = 0; i < 4; i++)
                {
                    var handle = new ClientHandle(
                        endpoint, root, Path.Combine(outboxRoot, "player-" + i))
                    {
                        Role = "PLAYER",
                        AccountId = playerGuests[i].AccountId,
                        AssignmentId = playerGuests[i].AssignmentId ?? string.Empty,
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

                if (includeSpectator)
                {
                    spectator = new ClientHandle(
                        endpoint, root, Path.Combine(outboxRoot, "spectator"))
                    {
                        Role = "SPECTATOR",
                        AccountId = admittedGuests[4].AccountId,
                        AssignmentId = admittedGuests[4].AssignmentId ?? string.Empty,
                        Seat = -1
                    };
                    await spectator.OpenAsync(tickets[4].Ticket, TimeSpan.FromSeconds(20), CancellationToken.None)
                        .ConfigureAwait(false);
                    await spectator.WaitForAsync(
                        () => spectator.LatestPublic != null,
                        TimeSpan.FromSeconds(20),
                        "spectator public baseline").ConfigureAwait(false);
                }

                if (exerciseExpiredAssignment)
                {
                    GuestState unauthorizedSpectator = guests[5 + (includeSpectator ? 1 : 0)];
                    await B1Flows.SpectateAsync(
                        lobby,
                        unauthorizedSpectator,
                        roomId,
                        "b2-unauthorized-spectator-" + Guid.NewGuid().ToString("N"),
                        CancellationToken.None).ConfigureAwait(false);
                    B1Flows.IssuedTicket spectatorTicket = await B1Flows.IssueTicketAsync(
                        lobby,
                        unauthorizedSpectator,
                        "b2-unauthorized-spectator-ticket-" + Guid.NewGuid().ToString("N"),
                        CancellationToken.None).ConfigureAwait(false);
                    await using (var rawSpectator = new RawWssProbe(endpoint, root))
                    {
                        await rawSpectator.AdmitAsync(
                            spectatorTicket.Ticket,
                            TimeSpan.FromSeconds(20),
                            CancellationToken.None).ConfigureAwait(false);
                        await players[0].WaitForAsync(
                            () => players[0].LatestPlayer?.PrivateView?.Decision?.Actions.Count > 0,
                            TimeSpan.FromSeconds(20),
                            "legal action for the unauthorized-action probe").ConfigureAwait(false);
                        PlayerSnapshot authorizedSnapshot = players[0].LatestPlayer!;
                        WireDecisionView decision = authorizedSnapshot.PrivateView!.Decision;
                        WireLegalActionView action = decision.Actions[0];
                        BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                            authorizedSnapshot.RoundId,
                            decision.DecisionId,
                            decision.ControlEpoch,
                            (ProtocolActionType)action.ActionType,
                            optionId: string.IsNullOrEmpty(action.OptionId) ? null : action.OptionId,
                            tileIds: action.TileIds.Count == 0 ? null : action.TileIds.ToArray());
                        var unauthorizedCommand = new Mahjong.Contracts.Wire.Command
                        {
                            Type = Mahjong.Contracts.Wire.CommandType.SubmitAction,
                            CanonicalPayload = Google.Protobuf.ByteString.CopyFrom(payload.CopyBytes()),
                            PayloadSchemaVersion = CanonicalPayloadDigest.FormatVersion,
                            ExpectedStateVersion = authorizedSnapshot.StateVersion,
                            ExpectedRoundId = authorizedSnapshot.RoundId,
                            ExpectedDecisionId = decision.DecisionId
                        };
                        Envelope refusal = await rawSpectator.SendCommandAsync(
                            "b2-unauthorized-action-" + Guid.NewGuid().ToString("N"),
                            unauthorizedCommand,
                            decision.ControlEpoch,
                            TimeSpan.FromSeconds(10),
                            CancellationToken.None).ConfigureAwait(false);
                        bool actionRefused = refusal.MessageType == MessageType.Error &&
                                             refusal.Error.Code == ProtocolErrorCode.Unauthenticated &&
                                             string.Equals(
                                                 refusal.Error.DetailCode,
                                                 "SCOPE_ROLE_NOT_AUTHORIZED",
                                                 StringComparison.Ordinal);
                        evidence["unauthorizedAction"] = new
                        {
                            response = refusal.MessageType.ToString(),
                            errorCode = refusal.Error?.Code.ToString() ?? string.Empty,
                            detailCode = refusal.Error?.DetailCode ?? string.Empty,
                            refused = actionRefused
                        };
                        if (!actionRefused)
                        {
                            throw new InvalidOperationException("A spectator action reached or passed the authority gate.");
                        }
                    }
                }

                if (exerciseExpiredAssignment)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.OptionalInt("confirmed-seat-wait-seconds", 16)))
                        .ConfigureAwait(false);
                    GuestState lateJoiner = await B1Flows.CreateGuestAsync(
                        lobby, "b2-late-joiner", CancellationToken.None).ConfigureAwait(false);
                    B1LobbyClient.LobbyHttpResult fullRoom = await lobby.PostAsync(
                        "/v1/rooms/" + Uri.EscapeDataString(roomId) + "/join",
                        new { preferSeat = 0 },
                        lateJoiner.SessionToken,
                        "b2-confirmed-seat-stays-occupied-" + Guid.NewGuid().ToString("N"),
                        CancellationToken.None).ConfigureAwait(false);
                    bool confirmedSeatsStillHeld = fullRoom.StatusCode == 409 &&
                                                   string.Equals(fullRoom.ErrorCode, "ROOM_FULL", StringComparison.Ordinal);
                    evidence["confirmedSeatRetention"] = new
                    {
                        waitedSeconds = options.OptionalInt("confirmed-seat-wait-seconds", 16),
                        status = fullRoom.StatusCode,
                        code = fullRoom.ErrorCode,
                        confirmedSeatsStillHeld
                    };
                    if (!confirmedSeatsStillHeld)
                    {
                        throw new InvalidOperationException("The Lobby expired a confirmed live seat and reassigned it.");
                    }
                }

                B1Flows.IssuedTicket? unusedTerminalTicket = null;

                ClientHandle observer = spectator ?? players[0];
                await observer.WaitForAsync(
                    () => includeSpectator
                        ? observer.LatestPublic != null
                        : observer.LatestPlayer?.PublicView != null,
                    TimeSpan.FromSeconds(20),
                    "public baseline").ConfigureAwait(false);

                evidence["admission"] = new
                {
                    guests = guests.Count,
                    players = players.Count,
                    seats,
                    distinctSessions = players.Select(p => p.Binding!.SessionId.Value)
                        .Concat(spectator == null ? Array.Empty<string>() : new[] { spectator.Binding!.SessionId.Value })
                        .Distinct().Count(),
                    spectator = includeSpectator,
                    spectatorSeat = spectator?.Seat ?? -1
                };

                var seen = new HashSet<string>(StringComparer.Ordinal);
                int actions = 0;
                int discards = 0;
                int passes = 0;
                int staleRejections = 0;
                int cheatOfferSkips = 0;
                string firstFailure = string.Empty;
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(durationSeconds);
                DateTimeOffset actionStart = DateTimeOffset.UtcNow;
                DateTimeOffset initialTicketExpiry = DateTimeOffset.Parse(
                    tickets[0].ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                bool renewalDone = false;
                int renewedSessions = 0;
                WirePublicMatchView? terminalView = null;
                string terminalRoundId = string.Empty;
                ulong terminalStateVersion = 0;
                if (exercisePublicReads)
                {
                    publicReadTask = Task.Run(async () =>
                    {
                        while (!publicReadLifetime.IsCancellationRequested)
                        {
                            try
                            {
                                B1LobbyClient.LobbyHttpResult read = await lobby.GetAsync(
                                    "/v1/rooms/" + Uri.EscapeDataString(roomId),
                                    publicReadLifetime.Token).ConfigureAwait(false);
                                Interlocked.Increment(ref publicReadCount);
                                if (read.StatusCode != 200)
                                {
                                    Interlocked.Increment(ref publicReadFailures);
                                }

                                await Task.Delay(25, publicReadLifetime.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (publicReadLifetime.IsCancellationRequested)
                            {
                                return;
                            }
                            catch (Exception)
                            {
                                Interlocked.Increment(ref publicReadFailures);
                                await Task.Delay(25, publicReadLifetime.Token).ConfigureAwait(false);
                            }
                        }
                    });
                }

                while (DateTimeOffset.UtcNow < deadline)
                {
                    if (renewAtSeconds > 0 && !renewalDone &&
                        DateTimeOffset.UtcNow - actionStart >= TimeSpan.FromSeconds(renewAtSeconds))
                    {
                        for (int i = 0; i < players.Count; i++)
                        {
                            B1Flows.IssuedTicket renewedTicket = await B1Flows.IssueTicketAsync(
                                lobby,
                                playerGuests[i],
                                "b2-renew-" + i + "-" + Guid.NewGuid().ToString("N"),
                                CancellationToken.None).ConfigureAwait(false);
                            var renewed = new ClientHandle(
                                endpoint, root, Path.Combine(outboxRoot, "renewed-player-" + i))
                            {
                                Role = "PLAYER",
                                AccountId = playerGuests[i].AccountId,
                                AssignmentId = playerGuests[i].AssignmentId ?? string.Empty,
                                Seat = seats[i]
                            };
                            await renewed.OpenAsync(
                                renewedTicket.Ticket, TimeSpan.FromSeconds(20), CancellationToken.None)
                                .ConfigureAwait(false);
                            await renewed.WaitForAsync(
                                () => renewed.LatestPlayer?.PrivateView != null &&
                                      string.Equals(renewed.LatestPlayer.RoundId, roomId + "-r1", StringComparison.Ordinal),
                                TimeSpan.FromSeconds(20),
                                "renewed current-assignment private baseline").ConfigureAwait(false);
                            ClientHandle previous = players[i];
                            players[i] = renewed;
                            tickets[i] = renewedTicket;
                            if (spectator == null && i == 0)
                            {
                                observer = renewed;
                            }

                            await previous.DisposeAsync().ConfigureAwait(false);
                            renewedSessions++;
                        }

                        renewalDone = true;
                    }

                    WirePublicMatchView? publicNow;
                    string publicRoundId;
                    ulong publicVersion;
                    if (includeSpectator)
                    {
                        PublicSnapshot? snapshot = observer.LatestPublic;
                        publicNow = snapshot?.View;
                        publicRoundId = snapshot?.RoundId ?? string.Empty;
                        publicVersion = snapshot?.StateVersion ?? 0UL;
                    }
                    else
                    {
                        PlayerSnapshot? snapshot = observer.LatestPlayer;
                        publicNow = snapshot?.PublicView;
                        publicRoundId = snapshot?.RoundId ?? string.Empty;
                        publicVersion = snapshot?.StateVersion ?? 0UL;
                    }

                    if (publicNow?.Board?.Phase == GamePhase.RoundEnd)
                    {
                        terminalView = publicNow;
                        terminalRoundId = publicRoundId;
                        terminalStateVersion = publicVersion;
                        break;
                    }

                    if (exerciseCapacityReuse && unusedTerminalTicket == null &&
                        publicNow?.Board?.WallRemaining <= 20)
                    {
                        unusedTerminalTicket = await B1Flows.IssueTicketAsync(
                            lobby, playerGuests[0], "b2-terminal-unused-" + Guid.NewGuid().ToString("N"),
                            CancellationToken.None).ConfigureAwait(false);
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
                    else if (actionDelayMs > 0)
                    {
                        await Task.Delay(actionDelayMs).ConfigureAwait(false);
                    }
                }

                evidence["actions"] = actions;
                evidence["discards"] = discards;
                evidence["passes"] = passes;
                publicReadLifetime.Cancel();
                await publicReadTask.ConfigureAwait(false);
                evidence["concurrentPublicReads"] = new
                {
                    enabled = exercisePublicReads,
                    count = publicReadCount,
                    failed = publicReadFailures,
                    lobbyOwnedResponses = !exercisePublicReads || publicReadCount > 0 && publicReadFailures == 0
                };
                if (exercisePublicReads && (publicReadCount == 0 || publicReadFailures != 0))
                {
                    throw new InvalidOperationException("Concurrent public room reads did not remain available during play.");
                }
                evidence["staleRejections"] = staleRejections;
                evidence["cheatOfferSkips"] = cheatOfferSkips;
                evidence["terminalReached"] = terminalView != null;
                bool renewalAfterInitialTicketExpiry = renewAtSeconds <= 0 ||
                    (renewalDone && renewedSessions == 4 && DateTimeOffset.UtcNow > initialTicketExpiry);
                evidence["ticketLifecycle"] = new
                {
                    initialTicketExpiresAt = initialTicketExpiry,
                    renewAtSeconds,
                    renewedSessions,
                    terminalAfterInitialTicketExpiry = DateTimeOffset.UtcNow > initialTicketExpiry,
                    renewalAfterInitialTicketExpiry
                };
                if (!renewalAfterInitialTicketExpiry)
                {
                    throw new InvalidOperationException("The match did not continue past the original ticket expiry through current-assignment renewal.");
                }
                bool? postUnchanged = null;
                bool? postNoRepublish = null;
                bool allClientsSawSameSettlement = false;
                if (terminalView != null)
                {
                    WirePublicMatchView view = terminalView;
                    await Task.WhenAll(players.Select(player => player.WaitForAsync(
                        () => player.LatestPlayer is { PublicView.Board.Phase: GamePhase.RoundEnd } latest &&
                              string.Equals(latest.RoundId, terminalRoundId, StringComparison.Ordinal) &&
                              string.Equals(
                                  latest.PublicView.SettlementRoundId,
                                  terminalRoundId,
                                  StringComparison.Ordinal),
                        TimeSpan.FromSeconds(15),
                        "player seat received this round's authoritative settlement"))).ConfigureAwait(false);
                    var clientSettlementFacts = new List<object>(players.Count);
                    allClientsSawSameSettlement = true;
                    string expectedSettlement = JsonSerializer.Serialize(view.SettlementEvents);
                    long[] expectedScores = view.Board.Scores.Select(score => score.Score).ToArray();
                    foreach (ClientHandle player in players)
                    {
                        var seatSnapshot = player.LatestPlayer;
                        WirePublicMatchView? seatView = seatSnapshot?.PublicView;
                        string actualSettlement = seatView == null
                            ? string.Empty
                            : JsonSerializer.Serialize(seatView.SettlementEvents);
                        long[] actualScores = seatView?.Board?.Scores.Select(score => score.Score).ToArray()
                            ?? Array.Empty<long>();
                        bool sameSettlement = seatSnapshot != null && seatView != null &&
                                              string.Equals(seatSnapshot.RoundId, terminalRoundId, StringComparison.Ordinal) &&
                                              seatSnapshot.StateVersion == terminalStateVersion &&
                                              string.Equals(seatView.SettlementRoundId, terminalRoundId, StringComparison.Ordinal) &&
                                              seatView.Board.Phase == GamePhase.RoundEnd &&
                                              EvaluateSettlementSemantics(
                                                  expectedSettlement,
                                                  actualSettlement,
                                                  expectedScores,
                                                  actualScores,
                                                  view.SettlementEvents.Count,
                                                  seatView.SettlementEvents.Count);
                        allClientsSawSameSettlement &= sameSettlement;
                        clientSettlementFacts.Add(new
                        {
                            seat = player.Seat,
                            roundId = seatSnapshot?.RoundId ?? string.Empty,
                            settlementRoundId = seatView?.SettlementRoundId ?? string.Empty,
                            phase = seatView?.Board?.Phase.ToString() ?? string.Empty,
                            settlementEventCount = seatView?.SettlementEvents.Count ?? 0,
                            stateVersion = seatSnapshot?.StateVersion ?? 0UL,
                            settlementEventSha256 = actualSettlement.Length == 0
                                ? string.Empty
                                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actualSettlement))),
                            sameSettlement
                        });
                    }
                    evidence["clientSettlements"] = clientSettlementFacts;
                    evidence["allClientsSawSameSettlement"] = allClientsSawSameSettlement;
                    if (!allClientsSawSameSettlement)
                    {
                        throw new InvalidOperationException("The four player clients did not receive the same authoritative settlement.");
                    }

                    evidence["terminal"] = new
                    {
                        stateVersion = terminalStateVersion,
                        roundId = terminalRoundId,
                        phase = view.Board.Phase.ToString(),
                        wallRemaining = view.Board.WallRemaining,
                        settlementRoundId = view.SettlementRoundId,
                        settlementEventTypes = view.SettlementEvents.Select(e => e.EventType.ToString()).ToArray(),
                        finalScores = view.Board.Scores.Select(s => s.Score).ToArray(),
                        sessionFinished = view.Session?.Finished ?? false,
                        spectatorPrivacyViolated = spectator?.PrivacyViolated ?? false
                    };

                    // Multiple authoritative ticks after the terminal must not change the
                    // terminal version or add another settlement event.
                    int terminalEvents = view.SettlementEvents.Count;
                    await Task.Delay(1500).ConfigureAwait(false);
                    await observer.WaitForAsync(
                        () => (includeSpectator
                                ? observer.LatestPublic?.StateVersion
                                : observer.LatestPlayer?.StateVersion) == terminalStateVersion,
                        TimeSpan.FromSeconds(5),
                        "post-terminal stable version").ConfigureAwait(false);
                    ulong? laterVersion = includeSpectator
                        ? observer.LatestPublic?.StateVersion
                        : observer.LatestPlayer?.StateVersion;
                    WirePublicMatchView? laterView = includeSpectator
                        ? observer.LatestPublic?.View
                        : observer.LatestPlayer?.PublicView;
                    postUnchanged = laterVersion == terminalStateVersion;
                    postNoRepublish = laterVersion.HasValue &&
                                      laterVersion == terminalStateVersion &&
                                      laterView?.SettlementEvents.Count == terminalEvents;
                    evidence["postTerminal"] = new
                    {
                        stateVersion = laterVersion ?? 0UL,
                        unchanged = postUnchanged,
                        settlementEvents = laterView?.SettlementEvents.Count ?? -1,
                        noRepublish = postNoRepublish
                    };

                    // The lobby must reconcile the table's public terminal projection before
                    // releasing seats or issuing a later admission. This is especially
                    // important for the supported in-memory deployment, where no database
                    // projection is shared between the two processes.
                    if (requireLobbyTerminal)
                    {
                    B1LobbyClient.LobbyHttpResult reconciled = await lobby.PostAsync(
                        "/v1/rooms/" + Uri.EscapeDataString(roomId) + "/reconcile",
                        null,
                        guests[0].SessionToken,
                        "b2-reconcile-" + Guid.NewGuid().ToString("N"),
                        CancellationToken.None).ConfigureAwait(false);
                    bool lobbyClosed = reconciled.StatusCode == 200 &&
                                       string.Equals(
                                           reconciled.Json.GetProperty("state").GetString(),
                                           "CLOSED",
                                           StringComparison.Ordinal);
                    B1LobbyClient.LobbyHttpResult postTerminalJoin = await lobby.PostAsync(
                        "/v1/rooms/" + Uri.EscapeDataString(roomId) + "/join",
                        new { preferSeat = 0 },
                        guests[1].SessionToken,
                        "b2-post-terminal-join-" + Guid.NewGuid().ToString("N"),
                        CancellationToken.None).ConfigureAwait(false);
                    bool laterJoinRefused = postTerminalJoin.StatusCode == 409 &&
                                            string.Equals(postTerminalJoin.ErrorCode, "ROOM_CLOSED", StringComparison.Ordinal);
                    evidence["lobbyTerminalReconciliation"] = new
                    {
                        reconcileStatus = reconciled.StatusCode,
                        roomClosed = lobbyClosed,
                        laterJoinStatus = postTerminalJoin.StatusCode,
                        laterJoinRefused = laterJoinRefused
                    };
                    if (!lobbyClosed || !laterJoinRefused)
                    {
                        throw new InvalidOperationException("The lobby did not retain the authoritative table terminal state.");
                    }

                    if (exerciseCapacityReuse)
                    {
                        if (unusedTerminalTicket == null)
                        {
                            throw new InvalidOperationException("The capacity probe has no retained terminal ticket.");
                        }

                        string? oldRoomTicketCode = await ProbeAdmissionRejectionAsync(
                            endpoint, root, unusedTerminalTicket.Ticket).ConfigureAwait(false);
                        bool oldRoomTicketRejected = IsAdmissionDenied(oldRoomTicketCode);
                        (string nextRoomId, string nextAssignmentId, int nextSeat) = await B1Flows.CreateRoomAsync(
                            lobby, guests[0], "B2 follow-up capacity probe",
                            "b2-next-room-" + Guid.NewGuid().ToString("N"), CancellationToken.None)
                            .ConfigureAwait(false);
                        if (nextSeat != 0 || string.IsNullOrEmpty(nextAssignmentId))
                        {
                            throw new InvalidOperationException("The lobby did not allocate the follow-up room's first seat.");
                        }

                        B1Flows.IssuedTicket nextTicket = await B1Flows.IssueTicketAsync(
                            lobby, guests[0], "b2-next-ticket-" + Guid.NewGuid().ToString("N"),
                            CancellationToken.None).ConfigureAwait(false);
                        Uri nextEndpoint = new Uri("wss://127.0.0.1:" + tablePort + "/v1/table?room=" +
                                                   Uri.EscapeDataString(nextRoomId));
                        await using (var nextPlayer = new ClientHandle(
                                         nextEndpoint, root, Path.Combine(outboxRoot, "follow-up-room"))
                                     {
                                         Role = "PLAYER",
                                         AccountId = guests[0].AccountId,
                                         AssignmentId = nextAssignmentId,
                                         Seat = nextSeat
                                     })
                        {
                            await nextPlayer.OpenAsync(nextTicket.Ticket, TimeSpan.FromSeconds(20), CancellationToken.None)
                                .ConfigureAwait(false);
                            await nextPlayer.WaitForAsync(
                                () => nextPlayer.LatestPlayer?.PrivateView != null,
                                TimeSpan.FromSeconds(20),
                                "follow-up room admission after terminal runtime retirement").ConfigureAwait(false);
                        }

                        evidence["roomCapacityReuse"] = new
                        {
                            maxRooms = 1,
                            firstRoomRetired = true,
                            followUpRoomAccepted = true,
                            followUpRoomId = nextRoomId,
                            oldRoomTicketCode,
                            oldRoomTicketRejected,
                            oldRoomCannotRecreateRuntime = oldRoomTicketRejected
                        };
                        if (!oldRoomTicketRejected)
                        {
                            throw new InvalidOperationException("A terminal room ticket recreated or entered the retired runtime.");
                        }
                    }
                    }
                    else
                    {
                        evidence["lobbyTerminalReconciliation"] = new { required = false, skippedForExistingRunnerCompatibility = true };
                    }
                }

                bool playersPrivacyViolated = players.Any(p => p.PrivacyViolated);
                evidence["playersPrivacyViolated"] = playersPrivacyViolated;
                evidence["spectatorPrivacyViolated"] = spectator?.PrivacyViolated ?? false;
                if (terminalView == null)
                {
                    WriteJson(outputPath, evidence);
                    Console.WriteLine("B2-TERMINAL-NATURAL BLOCKED room=" + roomId +
                                      " actions=" + actions);
                    return ExitBlocked;
                }

                bool ok = EvaluateNaturalSuccess(
                    terminalReached: true,
                    spectatorPrivacyViolated: spectator?.PrivacyViolated ?? false,
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
                publicReadLifetime.Cancel();
                try
                {
                    await publicReadTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

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

        private static string TamperSignature(string ticket)
        {
            int signatureStart = ticket.LastIndexOf('.') + 1;
            if (signatureStart <= 0 || signatureStart >= ticket.Length)
            {
                throw new ArgumentException("The ticket is not a compact signed token.", nameof(ticket));
            }

            char replacement = ticket[signatureStart] == 'A' ? 'B' : 'A';
            return ticket.Substring(0, signatureStart) + replacement + ticket.Substring(signatureStart + 1);
        }

        private static async Task<string?> ProbeAdmissionRejectionAsync(
            Uri endpoint,
            X509Certificate2 trustedRoot,
            string ticket)
        {
            await using var probe = new RawWssProbe(endpoint, trustedRoot);
            try
            {
                await probe.AdmitAsync(ticket, TimeSpan.FromSeconds(10), CancellationToken.None)
                    .ConfigureAwait(false);
                return null;
            }
            catch (RawWssProbe.ProbeRejectedException rejected)
            {
                return rejected.Code;
            }
        }

        private static bool IsAdmissionDenied(string? code)
        {
            return string.Equals(code, "WS_CLOSE_4401", StringComparison.Ordinal) ||
                   string.Equals(code, "INVALID_TOKEN", StringComparison.Ordinal);
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

        public static bool EvaluateSettlementSemantics(
            string expectedEvents,
            string actualEvents,
            IReadOnlyList<long> expectedScores,
            IReadOnlyList<long> actualScores,
            int expectedEventCount,
            int actualEventCount)
        {
            return expectedEventCount > 0 &&
                   expectedEventCount == actualEventCount &&
                   !string.IsNullOrEmpty(expectedEvents) &&
                   string.Equals(expectedEvents, actualEvents, StringComparison.Ordinal) &&
                   expectedScores.Count == 4 &&
                   expectedScores.SequenceEqual(actualScores);
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
            Check("settlement-semantic-good", EvaluateSettlementSemantics(
                "[{\"event\":\"DrawGame\"},{\"event\":\"RoundEnded\"}]",
                "[{\"event\":\"DrawGame\"},{\"event\":\"RoundEnded\"}]",
                new long[] { 25000, 25000, 25000, 25000 },
                new long[] { 25000, 25000, 25000, 25000 },
                2, 2), true);
            Check("settlement-same-count-scores-different-events", EvaluateSettlementSemantics(
                "[{\"event\":\"DrawGame\"},{\"event\":\"RoundEnded\"}]",
                "[{\"event\":\"Win\"},{\"event\":\"RoundEnded\"}]",
                new long[] { 25000, 25000, 25000, 25000 },
                new long[] { 25000, 25000, 25000, 25000 },
                2, 2), false);
            Check("settlement-empty-events", EvaluateSettlementSemantics(
                "[]", "[]", new long[] { 25000, 25000, 25000, 25000 },
                new long[] { 25000, 25000, 25000, 25000 }, 0, 0), false);
            Check("natural-default-keeps-existing-pg-gate-off",
                Options.Parse(Array.Empty<string>()).OptionalBool("require-lobby-terminal", false), false);
            Check("mvp-explicitly-enables-lobby-terminal-gate",
                Options.Parse(new[] { "--require-lobby-terminal", "true" })
                    .OptionalBool("require-lobby-terminal", false), true);
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

            public bool OptionalBool(string name, bool fallback)
            {
                if (!_values.TryGetValue(name, out string? value))
                {
                    return fallback;
                }

                return bool.TryParse(value, out bool parsed)
                    ? parsed
                    : throw new ArgumentException("--" + name + " must be true or false.");
            }
        }
    }
}
