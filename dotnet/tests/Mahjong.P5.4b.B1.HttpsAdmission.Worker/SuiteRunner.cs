using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using WireDecisionView = Mahjong.Contracts.Wire.DecisionView;
using WireLegalActionView = Mahjong.Contracts.Wire.LegalActionView;
using WireReceiptStatus = Mahjong.Contracts.Wire.ReceiptStatus;
using WirePlayerPublicView = Mahjong.Contracts.Wire.PlayerPublicView;
using WirePublicBoardView = Mahjong.Contracts.Wire.PublicBoardView;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    public static class SuiteRunner
    {
        public static async Task<int> RunAsync(Program.Options options)
        {
            string lobbyUrl = options.Require("lobby-url");
            int tablePort = int.Parse(options.Require("table-port"), CultureInfo.InvariantCulture);
            string caRootPath = options.Require("ca-root");
            string outputPath = options.Require("out");
            string secretsDirectory = options.Require("secrets");
            string outboxRoot = options.Require("outbox-root");
            string crashOut = options.Require("crash-out");
            string crashStarted = options.Require("crash-started");
            string recoverOut = options.Require("recover-out");
            string tlsDirectory = options.Require("tls-dir");
            string dotnet = options.Optional("dotnet") ?? Environment.ProcessPath!;
            string workerDll = options.Optional("worker-dll") ?? Environment.GetCommandLineArgs()[0];

            Directory.CreateDirectory(secretsDirectory);
            Directory.CreateDirectory(outboxRoot);
            var suite = new SuiteEvidence();
            var clients = new List<ClientHandle>();
            var autoplay = new List<AutoPlayer>();
            var autoplayCancellation = new CancellationTokenSource();
            Process? crashProcess = null;
            Process? recoverProcess = null;
            try
            {
                using var root = new X509Certificate2(caRootPath);
                using var lobby = new B1LobbyClient(new Uri(lobbyUrl), root);

                B1LobbyClient.LobbyHttpResult ready = await lobby.GetAsync("/health/ready", CancellationToken.None)
                    .ConfigureAwait(false);
                B1Flows.RequireStatus(ready, 200, "lobby readiness");
                Require(
                    string.Equals(B1Flows.RequireString(ready.Json, "status"), "ready", StringComparison.Ordinal),
                    "Lobby readiness body is not ready.");
                Require(
                    string.Equals(B1Flows.RequireString(ready.Json, "storage"), "configured", StringComparison.Ordinal),
                    "Lobby is not using the configured Postgres storage.");
                suite.Assertions.Add("lobby_ready");

                GuestState p0 = await B1Flows.CreateGuestAsync(lobby, "b1-p0", CancellationToken.None).ConfigureAwait(false);
                GuestState p1 = await B1Flows.CreateGuestAsync(lobby, "b1-p1", CancellationToken.None).ConfigureAwait(false);
                GuestState p2 = await B1Flows.CreateGuestAsync(lobby, "b1-p2", CancellationToken.None).ConfigureAwait(false);
                GuestState p3 = await B1Flows.CreateGuestAsync(lobby, "b1-p3", CancellationToken.None).ConfigureAwait(false);
                GuestState spectator = await B1Flows.CreateGuestAsync(lobby, "b1-s0", CancellationToken.None)
                    .ConfigureAwait(false);
                suite.Assertions.Add("five_real_guest_sessions");

                string createKey = "create-" + Guid.NewGuid().ToString("N");
                string title = "B1 " + Guid.NewGuid().ToString("N").Substring(0, 8);
                (string roomId, string hostAssignment, int hostSeat) = await B1Flows.CreateRoomAsync(
                    lobby,
                    p0,
                    title,
                    createKey,
                    CancellationToken.None).ConfigureAwait(false);
                suite.RoomId = roomId;
                Require(hostSeat == 0, "The room creator did not receive seat 0.");
                Require(!string.IsNullOrEmpty(hostAssignment), "The room creator has no assignment.");

                B1LobbyClient.LobbyHttpResult createReplay = await lobby.PostAsync(
                    "/v1/rooms",
                    new { title, maxPlayers = 4, visibility = "PUBLIC", allowSpectators = true },
                    p0.SessionToken,
                    createKey,
                    CancellationToken.None).ConfigureAwait(false);
                Require(createReplay.StatusCode == 201 || createReplay.StatusCode == 200,
                    "The same-key create replay did not return 200/201.");
                suite.HttpIdempotencyCreateReplay = createReplay.Replayed &&
                                                      string.Equals(
                                                          B1Flows.RequireString(createReplay.Json.GetProperty("room"), "roomId"),
                                                          roomId,
                                                          StringComparison.Ordinal) &&
                                                      string.Equals(
                                                          B1Flows.RequireString(
                                                              createReplay.Json.GetProperty("assignment"),
                                                              "assignmentId"),
                                                          hostAssignment,
                                                          StringComparison.Ordinal);
                Require(suite.HttpIdempotencyCreateReplay,
                    "The same-key create replay did not return the original room/assignment.");

                B1LobbyClient.LobbyHttpResult createConflict = await lobby.PostAsync(
                    "/v1/rooms",
                    new { title = title + "x", maxPlayers = 4, visibility = "PUBLIC", allowSpectators = true },
                    p0.SessionToken,
                    createKey,
                    CancellationToken.None).ConfigureAwait(false);
                suite.HttpIdempotencyCreateConflict =
                    createConflict.StatusCode == 409 &&
                    string.Equals(createConflict.ErrorCode, "REQUEST_ID_CONFLICT", StringComparison.Ordinal);
                Require(suite.HttpIdempotencyCreateConflict,
                    "The different-payload create did not fail with HTTP 409/REQUEST_ID_CONFLICT.");

                B1LobbyClient.LobbyHttpResult differentKindConflict = await lobby.PostAsync(
                    "/v1/rooms/" + Uri.EscapeDataString(roomId) + "/join",
                    new { preferSeat = 0 },
                    p0.SessionToken,
                    createKey,
                    CancellationToken.None).ConfigureAwait(false);
                suite.HttpDifferentKindConflict =
                    differentKindConflict.StatusCode == 409 &&
                    string.Equals(differentKindConflict.ErrorCode, "REQUEST_ID_CONFLICT", StringComparison.Ordinal);
                Require(suite.HttpDifferentKindConflict,
                    "The same-key different-API request did not fail with HTTP 409/REQUEST_ID_CONFLICT.");

                string p1JoinKey = "join1-" + Guid.NewGuid().ToString("N");
                (string p1Assignment, int p1Seat) = await B1Flows.JoinRoomAsync(
                    lobby,
                    p1,
                    roomId,
                    1,
                    p1JoinKey,
                    CancellationToken.None).ConfigureAwait(false);
                Require(p1Seat == 1, "Player 1 did not receive seat 1.");

                B1LobbyClient.LobbyHttpResult joinReplay = await lobby.PostAsync(
                    "/v1/rooms/" + Uri.EscapeDataString(roomId) + "/join",
                    new { preferSeat = 1 },
                    p1.SessionToken,
                    p1JoinKey,
                    CancellationToken.None).ConfigureAwait(false);
                Require(joinReplay.StatusCode == 200 || joinReplay.StatusCode == 201,
                    "The same-key join replay did not return 200/201.");
                suite.HttpIdempotencyJoinReplay = joinReplay.Replayed &&
                                                   string.Equals(
                                                       B1Flows.RequireString(
                                                           joinReplay.Json.GetProperty("assignment"),
                                                           "assignmentId"),
                                                       p1Assignment,
                                                       StringComparison.Ordinal);
                Require(suite.HttpIdempotencyJoinReplay,
                    "The same-key join replay did not return the original assignment.");

                B1LobbyClient.LobbyHttpResult joinConflict = await lobby.PostAsync(
                    "/v1/rooms/" + Uri.EscapeDataString(roomId) + "/join",
                    new { preferSeat = 3 },
                    p1.SessionToken,
                    p1JoinKey,
                    CancellationToken.None).ConfigureAwait(false);
                suite.HttpIdempotencyJoinConflict =
                    joinConflict.StatusCode == 409 &&
                    string.Equals(joinConflict.ErrorCode, "REQUEST_ID_CONFLICT", StringComparison.Ordinal);
                Require(suite.HttpIdempotencyJoinConflict,
                    "The different-payload join did not fail with HTTP 409/REQUEST_ID_CONFLICT.");

                (string p2Assignment, int p2Seat) = await B1Flows.JoinRoomAsync(
                    lobby,
                    p2,
                    roomId,
                    2,
                    "join2-" + Guid.NewGuid().ToString("N"),
                    CancellationToken.None).ConfigureAwait(false);
                (string p3Assignment, int p3Seat) = await B1Flows.JoinRoomAsync(
                    lobby,
                    p3,
                    roomId,
                    3,
                    "join3-" + Guid.NewGuid().ToString("N"),
                    CancellationToken.None).ConfigureAwait(false);
                Require(p2Seat == 2 && p3Seat == 3, "The player seats are not the requested 2/3.");
                string spectatorAssignment = await B1Flows.SpectateAsync(
                    lobby,
                    spectator,
                    roomId,
                    "spec-" + Guid.NewGuid().ToString("N"),
                    CancellationToken.None).ConfigureAwait(false);
                suite.PlayerCount = 4;
                suite.SpectatorCount = 1;
                suite.Assertions.Add("room_join_spectator_assignments");

                B1Flows.IssuedTicket p0Ticket = await B1Flows.IssueTicketAsync(
                    lobby,
                    p0,
                    "ticket0-" + Guid.NewGuid().ToString("N"),
                    CancellationToken.None).ConfigureAwait(false);
                B1Flows.IssuedTicket p1Ticket = await B1Flows.IssueTicketAsync(
                    lobby,
                    p1,
                    "ticket1-" + Guid.NewGuid().ToString("N"),
                    CancellationToken.None).ConfigureAwait(false);
                B1Flows.IssuedTicket p2Ticket = await B1Flows.IssueTicketAsync(
                    lobby,
                    p2,
                    "ticket2-" + Guid.NewGuid().ToString("N"),
                    CancellationToken.None).ConfigureAwait(false);
                B1Flows.IssuedTicket p3Ticket = await B1Flows.IssueTicketAsync(
                    lobby,
                    p3,
                    "ticket3-" + Guid.NewGuid().ToString("N"),
                    CancellationToken.None).ConfigureAwait(false);
                B1Flows.IssuedTicket spectatorTicket = await B1Flows.IssueTicketAsync(
                    lobby,
                    spectator,
                    "ticket-s0-" + Guid.NewGuid().ToString("N"),
                    CancellationToken.None).ConfigureAwait(false);
                Require(p0Ticket.RoomId == roomId && p2Ticket.RoomId == roomId && p3Ticket.RoomId == roomId,
                    "A player ticket is not bound to the created room.");
                Require(p0Ticket.AssignmentId == p0.AssignmentId, "The p0 ticket assignment is wrong.");
                Require(p2Ticket.AssignmentId == p2Assignment && p3Ticket.AssignmentId == p3Assignment,
                    "A player ticket assignment is wrong.");
                Require(spectatorTicket.Role == "SPECTATOR" && spectatorTicket.SeatId == -1,
                    "The spectator ticket does not carry the spectator role/no seat.");
                Require(!string.Equals(p0Ticket.Jti, p2Ticket.Jti, StringComparison.Ordinal),
                    "Two issued tickets share a jti.");
                suite.Assertions.Add("lobby_issued_five_binding_tickets");

                // Ticket idempotency is real HTTP: same key/body replays the same
                // ticket; same key/different assignment conflicts.
                string ticketIdemKey = "ticket-idem-" + Guid.NewGuid().ToString("N");
                B1Flows.IssuedTicket ticketIdem = await B1Flows.IssueTicketAsync(
                    lobby,
                    p0,
                    ticketIdemKey,
                    CancellationToken.None).ConfigureAwait(false);
                B1LobbyClient.LobbyHttpResult ticketReplay = await lobby.PostAsync(
                    "/v1/table-tickets",
                    new { assignmentId = p0.AssignmentId },
                    p0.SessionToken,
                    ticketIdemKey,
                    CancellationToken.None).ConfigureAwait(false);
                suite.HttpTicketReplay = (ticketReplay.StatusCode == 200 || ticketReplay.StatusCode == 201) &&
                                         ticketReplay.Replayed &&
                                         string.Equals(
                                             B1Flows.RequireString(ticketReplay.Json, "jti"),
                                             ticketIdem.Jti,
                                             StringComparison.Ordinal) &&
                                         string.Equals(
                                             B1Flows.RequireString(ticketReplay.Json, "assignmentId"),
                                             p0.AssignmentId,
                                             StringComparison.Ordinal) &&
                                         string.Equals(
                                             B1Flows.RequireString(ticketReplay.Json, "roomId"),
                                             roomId,
                                             StringComparison.Ordinal);
                Require(suite.HttpTicketReplay,
                    "The same-key ticket replay did not return the original jti/assignment/room.");

                B1LobbyClient.LobbyHttpResult ticketConflict = await lobby.PostAsync(
                    "/v1/table-tickets",
                    new { assignmentId = p2Assignment },
                    p0.SessionToken,
                    ticketIdemKey,
                    CancellationToken.None).ConfigureAwait(false);
                suite.HttpTicketConflict =
                    ticketConflict.StatusCode == 409 &&
                    string.Equals(ticketConflict.ErrorCode, "REQUEST_ID_CONFLICT", StringComparison.Ordinal);
                Require(suite.HttpTicketConflict,
                    "The different-assignment ticket did not fail with HTTP 409/REQUEST_ID_CONFLICT.");
                p0Ticket = ticketIdem;

                string tableUri = "wss://127.0.0.1:" + tablePort + "/v1/table?room=" + Uri.EscapeDataString(roomId);
                var p0Client = NewClient(tableUri, root, outboxRoot, "p0", p0);
                var p1Client = NewClient(tableUri, root, outboxRoot, "p1", p1);
                var p2Client = NewClient(tableUri, root, outboxRoot, "p2", p2);
                var p3Client = NewClient(tableUri, root, outboxRoot, "p3", p3);
                var spectatorClient = NewClient(tableUri, root, outboxRoot, "s0", spectator);
                clients.AddRange(new[] { p0Client, p1Client, p2Client, p3Client, spectatorClient });

                await p0Client.OpenAsync(p0Ticket.Ticket, TimeSpan.FromSeconds(40), CancellationToken.None)
                    .ConfigureAwait(false);
                await p1Client.OpenAsync(p1Ticket.Ticket, TimeSpan.FromSeconds(40), CancellationToken.None)
                    .ConfigureAwait(false);
                await p2Client.OpenAsync(p2Ticket.Ticket, TimeSpan.FromSeconds(40), CancellationToken.None)
                    .ConfigureAwait(false);
                await p3Client.OpenAsync(p3Ticket.Ticket, TimeSpan.FromSeconds(40), CancellationToken.None)
                    .ConfigureAwait(false);
                await spectatorClient.OpenAsync(spectatorTicket.Ticket, TimeSpan.FromSeconds(40), CancellationToken.None)
                    .ConfigureAwait(false);
                suite.AdmittedPlayers = 4;
                suite.SpectatorAdmitted = spectatorClient.State == TableConnectionState.Ready;
                Require(p0Client.Binding!.Role == ProtocolRole.Player && p0Client.Binding.SeatId == 0,
                    "p0 binding is not seat 0.");
                Require(p1Client.Binding!.SeatId == 1, "p1 binding is not seat 1.");
                Require(p2Client.Binding!.SeatId == 2 && p3Client.Binding!.SeatId == 3,
                    "p2/p3 bindings are not seats 2/3.");
                Require(spectatorClient.Binding!.Role == ProtocolRole.Spectator &&
                        spectatorClient.Binding.SeatId == ProtocolConstants.NoSeat,
                    "The spectator binding is not a no-seat spectator.");

                RequireBindingIdentity(p0Client, p0, roomId, ProtocolRole.Player, 0);
                RequireBindingIdentity(p1Client, p1, roomId, ProtocolRole.Player, 1);
                RequireBindingIdentity(p2Client, p2, roomId, ProtocolRole.Player, 2);
                RequireBindingIdentity(p3Client, p3, roomId, ProtocolRole.Player, 3);
                RequireBindingIdentity(spectatorClient, spectator, roomId, ProtocolRole.Spectator, ProtocolConstants.NoSeat);
                Require(
                    new[] { p0Client, p1Client, p2Client, p3Client, spectatorClient }
                        .Select(client => client.Binding!.SessionId.Value)
                        .Distinct(StringComparer.Ordinal)
                        .Count() == 5,
                    "The five admitted identities do not have five distinct session bindings.");
                Require(
                    new[] { p0Client, p1Client, p2Client, p3Client, spectatorClient }
                        .All(client => client.ConfirmedCursor.HasValue),
                    "A real Client has no independent confirmed stream cursor.");
                Require(
                    PublicProjectionIsSafe(p0Client) && PublicProjectionIsSafe(p1Client) &&
                    PublicProjectionIsSafe(p2Client) && PublicProjectionIsSafe(p3Client) &&
                    PublicProjectionIsSafe(spectatorClient),
                    "A public/player public projection exposed a private field name.");

                Require(p0Client.BaselineCount > 0 && p1Client.BaselineCount > 0 &&
                        p2Client.BaselineCount > 0 && p3Client.BaselineCount > 0 &&
                        spectatorClient.BaselineCount > 0,
                    "At least one real Client did not install a full baseline.");
                suite.BaselineFullForAll = true;
                suite.PrivateStreamsIsolated =
                    p0Client.LatestPlayer?.PrivateView?.Seat == 0 &&
                    p1Client.LatestPlayer?.PrivateView?.Seat == 1 &&
                    p2Client.LatestPlayer?.PrivateView?.Seat == 2 &&
                    p3Client.LatestPlayer?.PrivateView?.Seat == 3 &&
                    spectatorClient.LatestPlayer == null &&
                    spectatorClient.LatestPublic != null;
                Require(suite.PrivateStreamsIsolated, "The player/spectator private streams are not isolated.");
                suite.Assertions.Add("wss_baseline_and_stream_identity");

                (PlayerSnapshot normalSnapshot, WireDecisionView normalDecision, WireLegalActionView normalDiscard) =
                    await B1Actions.WaitForDiscardAsync(p0Client, TimeSpan.FromSeconds(60), CancellationToken.None)
                        .ConfigureAwait(false);
                string normalRequestId = "req-normal-" + Guid.NewGuid().ToString("N");
                p0Client.Loss.TargetRequestId = normalRequestId;
                p0Client.Loss.DropTargetReceipts = false;
                ProtocolCommand normalCommand = B1Actions.BuildCommand(
                    p0Client,
                    normalSnapshot,
                    normalDecision,
                    normalDiscard,
                    normalRequestId);
                string normalScopeId = p0Client.Binding.Scope.ScopeId.Value;
                suite.NormalRequestId = normalRequestId;
                TableReceiptResolvedEventArgs normalResolved;
                try
                {
                    normalResolved = await B1Actions.SubmitAndWaitAsync(
                        p0Client,
                        normalCommand,
                        TimeSpan.FromSeconds(20),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (B1Actions.ClientBlockedException blocked)
                {
                    suite.Blocked = true;
                    suite.BlockerCode = blocked.Code;
                    suite.BlockerDetail = Program.Redact(blocked.Message);
                    IReadOnlyList<ReceiptLossChannel.ReceiptObservation> observations =
                        p0Client.Loss.ObservedReceipts;
                    ReceiptLossChannel.ReceiptObservation observed = observations[observations.Count - 1];
                    suite.ServerReceiptObserved = true;
                    suite.ServerReceiptStatus = observed.Status.ToString();
                    suite.ServerReceiptCommandVersion = observed.CommandVersion ?? 0;
                    suite.ServerReceiptScopePresent = observed.ScopePresent;
                    suite.ClientPendingAfterServerReceipt =
                        (await p0Client.PendingAsync(CancellationToken.None).ConfigureAwait(false)).Count;
                    (bool reissueRejected, string reissueDetail) = await ProbeImmediateReissueAsync(
                        lobby,
                        root,
                        tablePort,
                        roomId,
                        p0).ConfigureAwait(false);
                    suite.ImmediateReissueRejected = reissueRejected;
                    suite.ImmediateReissueDetail = reissueDetail;
                    (bool found, ulong version, string detail) = await ProbeServerReceiptAsync(
                        lobby,
                        root,
                        tablePort,
                        roomId,
                        p0,
                        normalScopeId,
                        normalRequestId,
                        p0Ticket.ExpiresAt).ConfigureAwait(false);
                    suite.RawReceiptQueryFound = found;
                    suite.RawReceiptQueryCommandVersion = version;
                    suite.RawReceiptQueryDetail = detail;
                    await NegativeProbes.RunAsync(
                        suite,
                        lobby,
                        root,
                        tablePort,
                        normalScopeId,
                        normalRequestId,
                        tlsDirectory).ConfigureAwait(false);
                    suite.Assertions.Add("server_receipt_delivered_but_client_did_not_resolve");
                    Program.WriteJson(outputPath, suite);
                    Console.Error.WriteLine(
                        "B1-SUITE-BLOCKED " + suite.BlockerCode +
                        " serverReceipt=" + suite.ServerReceiptStatus +
                        "/" + suite.ServerReceiptCommandVersion +
                        " scopePresent=" + suite.ServerReceiptScopePresent +
                        " rawQueryFound=" + suite.RawReceiptQueryFound);
                    return Program.ExitBlocked;
                }
                Require(normalResolved.Resolution.ReceiptStatus == Mahjong.Contracts.ReceiptStatus.Applied &&
                        normalResolved.Resolution.CommandVersion.HasValue,
                    "The normal action did not produce an APPLIED command version.");
                suite.NormalActionSeat = p0Client.Seat;
                suite.NormalActionStateVersion = normalResolved.Resolution.CommandVersion!.Value.Value;
                suite.NormalRequestId = normalRequestId;

                IReadOnlyList<byte[]> normalFrames = p0Client.Loss.CapturedCommandFrames;
                Require(normalFrames.Count == 1, "The normal command bytes were not captured exactly once.");
                await p0Client.Transport.SendAsync(normalFrames[0], CancellationToken.None).ConfigureAwait(false);
                await WaitUntilAsync(
                    () => p0Client.Loss.ObservedReceipts.Count >= 2,
                    TimeSpan.FromSeconds(15),
                    "the exact-byte normal replay receipt").ConfigureAwait(false);
                ReceiptLossChannel.ReceiptObservation normalReplay = p0Client.Loss.ObservedReceipts[1];
                suite.NormalReplayObservedReceipts = p0Client.Loss.ObservedReceipts.Count;
                suite.NormalReplayCommandVersion = normalReplay.CommandVersion ?? 0;
                suite.NormalReplaySameReceipt =
                    normalReplay.Status == WireReceiptStatus.Applied &&
                    normalReplay.CommandVersion == normalResolved.Resolution.CommandVersion!.Value.Value &&
                    normalReplay.Replayed;
                Require(suite.NormalReplaySameReceipt,
                    "The exact-byte replay did not return the original APPLIED receipt identity.");
                Require((await p0Client.PendingAsync(CancellationToken.None).ConfigureAwait(false)).Count == 0,
                    "The normal command outbox did not clear.");
                suite.AllClientsSawNormalAction = await WaitForActionVisibleAsync(
                    new[] { p0Client, p1Client, p2Client, p3Client, spectatorClient },
                    p0Client.Seat,
                    normalDiscard.TileIds[0],
                    normalResolved.Resolution.CommandVersion!.Value.Value,
                    TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                Require(suite.AllClientsSawNormalAction, "Not every real client observed the normal action.");
                suite.Assertions.Add("real_action_applied_and_exact_replay_idempotent");

                GuestState crashGuest = CloneSecret(p1);
                await p1Client.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                string secretPath = Path.Combine(secretsDirectory, "player1-secret.json");
                Program.WriteJson(secretPath, crashGuest);
                string p1Outbox = Path.Combine(outboxRoot, "p1");
                Directory.CreateDirectory(p1Outbox);
                crashProcess = StartChild(
                    dotnet,
                    workerDll,
                    new[]
                    {
                        "crash-client",
                        "--lobby-url", lobbyUrl,
                        "--table-port", tablePort.ToString(CultureInfo.InvariantCulture),
                        "--ca-root", caRootPath,
                        "--secret", secretPath,
                        "--outbox", p1Outbox,
                        "--out", crashOut,
                        "--started", crashStarted
                    });
                await WaitForFileAsync(crashStarted, TimeSpan.FromSeconds(45), "the crash client admission marker")
                    .ConfigureAwait(false);
                Require(!crashProcess.HasExited, "The crash client exited before its admission marker was consumed.");

                autoplay.Add(new AutoPlayer(p0Client, "auto-p0", autoplayCancellation.Token));
                autoplay.Add(new AutoPlayer(p2Client, "auto-p2", autoplayCancellation.Token));
                autoplay.Add(new AutoPlayer(p3Client, "auto-p3", autoplayCancellation.Token));
                foreach (AutoPlayer player in autoplay)
                {
                    player.Start();
                }

                await WaitForFileAsync(crashOut, TimeSpan.FromSeconds(90), "the crash client evidence")
                    .ConfigureAwait(false);
                CrashClientEvidence crash = Program.ReadJson<CrashClientEvidence>(crashOut);
                suite.CrashPid = crash.Pid;
                suite.CrashCommandVersion = (int)crash.OriginalCommandVersion;
                suite.CrashRequestId = crash.RequestId;
                Require(crash.OriginalReceiptStatus == "Applied" && crash.ReplayReceiptStatus == "Applied",
                    "The crash command/replay were not APPLIED.");
                Require(crash.OriginalCommandVersion == crash.ReplayCommandVersion && crash.ReplayReplayed,
                    "The crash replay did not return the same receipt identity.");
                Require(crash.DroppedReceiptCount >= 2 && crash.CapturedCommandFrames == 1,
                    "The crash client did not lose exactly the documented final replies.");
                Require(crash.PendingBeforeKill == 1 && crash.HoldArmed, "The crash outbox was not held unresolved.");
                suite.Assertions.Add("crash_command_committed_and_receipt_lost");

                // Real OS kill: no graceful client close, no in-process object reuse.
                crashProcess.Kill(entireProcessTree: true);
                await crashProcess.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                Require(crashProcess.HasExited, "The crash client did not die from the real process kill.");
                suite.Assertions.Add("real_os_kill_client");

                autoplayCancellation.Cancel();
                foreach (AutoPlayer player in autoplay)
                {
                    await player.StopAsync().ConfigureAwait(false);
                }

                GuestState recoverGuest = CloneSecret(p1);
                Program.WriteJson(secretPath, recoverGuest);
                recoverProcess = StartChild(
                    dotnet,
                    workerDll,
                    new[]
                    {
                        "recover-client",
                        "--lobby-url", lobbyUrl,
                        "--table-port", tablePort.ToString(CultureInfo.InvariantCulture),
                        "--ca-root", caRootPath,
                        "--secret", secretPath,
                        "--outbox", p1Outbox,
                        "--out", recoverOut,
                        "--crash", crashOut
                    });
                await WaitForFileAsync(recoverOut, TimeSpan.FromSeconds(60), "the recovery evidence")
                    .ConfigureAwait(false);
                RecoverClientEvidence recovered = Program.ReadJson<RecoverClientEvidence>(recoverOut);
                suite.RecoverPid = recovered.NewPid;
                suite.RecoveryFound = recovered.ResolvedStatus == "Applied" &&
                                      recovered.ResolvedFromLookup &&
                                      (int)recovered.ResolvedCommandVersion == suite.CrashCommandVersion;
                suite.RecoveryPendingAfter = recovered.PendingAfterQuery;
                Require(recovered.NewPid != crash.Pid, "The recovery did not run in a new OS process.");
                Require(recovered.NewBootId != crash.BootId, "The recovery reused the crash boot id.");
                Require(recovered.BaselineBeforeQuery && recovered.BaselineCount >= 1,
                    "The recovery did not install a new full baseline before querying.");
                Require(recovered.PendingBeforeQuery == 1 && recovered.DeliveryUnknownBeforeQuery,
                    "The disk outbox was not reloaded as one unknown result.");
                Require(suite.RecoveryFound && recovered.PendingAfterQuery == 0 &&
                        recovered.OutboxEntriesAfter == 0,
                    "The Found receipt did not clear the disk outbox.");
                suite.Assertions.Add("new_pid_disk_outbox_receipt_query_found");

                autoplayCancellation.Dispose();
                bool liveSawCrashAction = await WaitForActionVisibleAsync(
                    new[] { p0Client, p2Client, p3Client, spectatorClient },
                    crash.Seat,
                    crash.DiscardTileId,
                    (ulong)suite.CrashCommandVersion,
                    TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                suite.RecoveredProjectionConfirmed =
                    recovered.RecoveredCrashActionVisible &&
                    recovered.RecoveredStateVersion >= suite.CrashCommandVersion &&
                    recovered.RecoveredPublicPlayerCount >= 4 &&
                    recovered.RecoveredSeat == crash.Seat &&
                    string.Equals(recovered.RecoveredRole, "Player", StringComparison.Ordinal);
                suite.AllClientsSawCrashAction = liveSawCrashAction && suite.RecoveredProjectionConfirmed;
                Require(suite.AllClientsSawCrashAction,
                    "The four live clients and the recovered process did not all confirm the crash action.");
                Require(!string.Equals(recovered.RecoveredSessionIdHash, Program.Sha256Hex(p1Client.Binding!.SessionId.Value),
                        StringComparison.Ordinal),
                    "The recovery reused the closed pre-crash session binding.");
                suite.Assertions.Add("multi_client_confirmation_after_recovery");

                // Negative probes run after the positive evidence so a deliberate
                // displacement cannot disturb the normal/observer assertions.
                await NegativeProbes.RunAsync(
                    suite,
                    lobby,
                    root,
                    tablePort,
                    normalScopeId,
                    normalRequestId,
                    tlsDirectory).ConfigureAwait(false);

                suite.Assertions.Add("negative_matrix_complete");
                Program.WriteJson(outputPath, suite);
                Console.WriteLine(
                    "B1-SUITE-OK room=" + roomId +
                    " normalVersion=" + suite.NormalActionStateVersion +
                    " crashVersion=" + suite.CrashCommandVersion +
                    " recovered=" + suite.RecoveryFound);
                return Program.ExitOk;
            }
            catch (Exception ex)
            {
                suite.Assertions.Add("FAILED: " + ex.GetType().Name + ": " + Program.Redact(ex.Message));
                try
                {
                    Program.WriteJson(outputPath, suite);
                }
                catch (Exception)
                {
                }

                Console.Error.WriteLine("B1-SUITE-FAIL " + ex.GetType().Name + ": " + Program.Redact(ex.Message));
                return Program.ExitFailed;
            }
            finally
            {
                if (crashProcess != null && !crashProcess.HasExited)
                {
                    try
                    {
                        crashProcess.Kill(entireProcessTree: true);
                    }
                    catch (Exception)
                    {
                    }
                }

                if (recoverProcess != null && !recoverProcess.HasExited)
                {
                    try
                    {
                        recoverProcess.Kill(entireProcessTree: true);
                    }
                    catch (Exception)
                    {
                    }
                }

                foreach (AutoPlayer player in autoplay)
                {
                    await player.StopAsync().ConfigureAwait(false);
                }

                foreach (ClientHandle client in clients)
                {
                    await client.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task<(bool Rejected, string Detail)> ProbeImmediateReissueAsync(
            B1LobbyClient lobby,
            X509Certificate2 root,
            int tablePort,
            string roomId,
            GuestState p0)
        {
            try
            {
                B1Flows.IssuedTicket reissued = await B1Flows.IssueTicketAsync(
                    lobby,
                    p0,
                    "reissue-probe-" + Guid.NewGuid().ToString("N"),
                    CancellationToken.None).ConfigureAwait(false);
                await using var probe = new RawWssProbe(
                    new Uri("wss://127.0.0.1:" + tablePort + "/v1/table?room=" + Uri.EscapeDataString(roomId)),
                    root);
                try
                {
                    await probe.AdmitAsync(reissued.Ticket, TimeSpan.FromSeconds(10), CancellationToken.None)
                        .ConfigureAwait(false);
                    return (false, "ADMITTED_WITH_A_FRESH_JTI");
                }
                catch (RawWssProbe.ProbeRejectedException ex)
                {
                    return (true, ex.Code);
                }
                catch (TableChannelClosedException ex)
                {
                    return (true, ex.Code);
                }
            }
            catch (Exception ex)
            {
                return (false, "PROBE_FAILED:" + ex.GetType().Name + ":" + Program.Redact(ex.Message));
            }
        }

        private static async Task<(bool Found, ulong Version, string Detail)> ProbeServerReceiptAsync(
            B1LobbyClient lobby,
            X509Certificate2 root,
            int tablePort,
            string roomId,
            GuestState p0,
            string scopeId,
            string requestId,
            string originalTicketExpiresAt)
        {
            try
            {
                // The accepted in-memory consumption registry refuses a second jti
                // for the same assignment until the previous ticket has expired. Wait
                // for that documented boundary, then prove the historical receipt is
                // still queryable by the same account/room/role.
                DateTimeOffset originalExpiry = DateTimeOffset.Parse(
                    originalTicketExpiresAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
                TimeSpan wait = originalExpiry.AddSeconds(6) - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait).ConfigureAwait(false);
                }

                B1Flows.IssuedTicket ticket = await B1Flows.IssueTicketAsync(
                    lobby,
                    p0,
                    "probe-p0-ticket-" + Guid.NewGuid().ToString("N"),
                    CancellationToken.None).ConfigureAwait(false);
                await using var probe = new RawWssProbe(
                    new Uri("wss://127.0.0.1:" + tablePort + "/v1/table?room=" + Uri.EscapeDataString(roomId)),
                    root);
                await probe.AdmitAsync(ticket.Ticket, TimeSpan.FromSeconds(15), CancellationToken.None)
                    .ConfigureAwait(false);
                Envelope lookup = await probe.QueryReceiptAsync(
                    scopeId,
                    requestId,
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None).ConfigureAwait(false);
                if (lookup.BodyCase == Envelope.BodyOneofCase.ReceiptLookup &&
                    lookup.ReceiptLookup != null &&
                    lookup.ReceiptLookup.Status == LookupStatus.Found &&
                    lookup.ReceiptLookup.Receipt != null)
                {
                    return (
                        true,
                        lookup.ReceiptLookup.Receipt.HasCommandVersion
                            ? lookup.ReceiptLookup.Receipt.CommandVersion
                            : 0,
                        lookup.ReceiptLookup.Receipt.Status.ToString());
                }

                return (false, 0, lookup.BodyCase.ToString());
            }
            catch (Exception ex)
            {
                return (false, 0, ex.GetType().Name + ":" + Program.Redact(ex.Message));
            }
        }

        private static void RequireBindingIdentity(
            ClientHandle client,
            GuestState guest,
            string roomId,
            ProtocolRole role,
            int seat)
        {
            TableSessionBinding binding = client.Binding
                ?? throw new InvalidOperationException("A real Client has no binding.");
            Require(string.Equals(binding.Scope.AccountId, guest.AccountId, StringComparison.Ordinal),
                "A Client scope account does not match its Lobby account.");
            Require(string.Equals(binding.RoomId.Value, roomId, StringComparison.Ordinal),
                "A Client binding room does not match the Lobby room.");
            Require(binding.Role == role && binding.SeatId == seat,
                "A Client binding role/seat does not match the Lobby assignment.");
            Require(binding.BindingEpoch.Value > 0 && binding.OwnerEpoch.Value > 0,
                "A Client binding epoch is not positive.");
        }

        private static bool PublicProjectionIsSafe(ClientHandle client)
        {
            string text = string.Empty;
            PublicSnapshot? publicSnapshot = client.LatestPublic;
            if (publicSnapshot != null)
            {
                text += publicSnapshot.ToString();
            }

            PlayerSnapshot? player = client.LatestPlayer;
            if (player?.PublicView != null)
            {
                text += player.PublicView.ToString();
            }

            string[] forbidden =
            {
                "closed_tiles", "drawn_tile_id", "cheat_points", "cheat_entries",
                "seed", "ura_dora", "sleeve"
            };
            foreach (string token in forbidden)
            {
                if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static ClientHandle NewClient(
            string url,
            X509Certificate2 root,
            string outboxRoot,
            string name,
            GuestState guest)
        {
            string directory = Path.Combine(outboxRoot, name);
            Directory.CreateDirectory(directory);
            var client = new ClientHandle(new Uri(url), root, directory)
            {
                AccountId = guest.AccountId,
                AssignmentId = guest.AssignmentId!,
                Role = guest.Role,
                Seat = guest.Seat
            };
            return client;
        }

        private static GuestState CloneSecret(GuestState guest)
        {
            return new GuestState
            {
                AccountId = guest.AccountId,
                DisplayName = guest.DisplayName,
                SessionToken = guest.SessionToken,
                DeviceId = guest.DeviceId,
                BootstrapSecret = string.Empty,
                RoomId = guest.RoomId,
                AssignmentId = guest.AssignmentId,
                Role = guest.Role,
                Seat = guest.Seat
            };
        }

        private static Process StartChild(string dotnet, string workerDll, string[] arguments)
        {
            var info = new ProcessStartInfo
            {
                FileName = dotnet,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            info.ArgumentList.Add(workerDll);
            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            return Process.Start(info)
                   ?? throw new InvalidOperationException("Could not start the B1 child process.");
        }

        private static async Task WaitForFileAsync(string path, TimeSpan timeout, string what)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (File.Exists(path))
                {
                    // Wait for the atomic JSON write to settle.
                    await Task.Delay(50).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(50).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for " + what + " at " + path + ".");
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string what)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (predicate())
                {
                    return;
                }

                await Task.Delay(25).ConfigureAwait(false);
            }

            if (!predicate())
            {
                throw new TimeoutException("Timed out waiting for " + what + ".");
            }
        }

        private static async Task<bool> WaitForActionVisibleAsync(
            IEnumerable<ClientHandle> clients,
            int seat,
            uint tileId,
            ulong minimumStateVersion,
            TimeSpan timeout)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                bool allVisible = true;
                foreach (ClientHandle client in clients)
                {
                    WirePublicBoardView? board = PublicBoard(client);
                    if (board == null || StateVersion(client) < minimumStateVersion)
                    {
                        allVisible = false;
                        break;
                    }

                    WirePlayerPublicView? player = board.Players.FirstOrDefault(item => item.Seat == seat);
                    if (player == null || !player.Discards.Any(discard => discard.TileId == tileId))
                    {
                        allVisible = false;
                        break;
                    }
                }

                if (allVisible)
                {
                    return true;
                }

                await Task.Delay(25).ConfigureAwait(false);
            }

            return false;
        }

        private static WirePublicBoardView? PublicBoard(ClientHandle client)
        {
            PlayerSnapshot? player = client.LatestPlayer;
            if (player?.PublicView?.Board != null)
            {
                return player.PublicView.Board;
            }

            return client.LatestPublic?.View?.Board;
        }

        private static ulong StateVersion(ClientHandle client)
        {
            PlayerSnapshot? player = client.LatestPlayer;
            if (player != null)
            {
                return player.StateVersion;
            }

            return client.LatestPublic?.StateVersion ?? 0;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    /// <summary>
    /// Fast legal play for the OTHER connected seats so the four-seat rotation
    /// reaches the crash seat inside the short reservation window. It only
    /// submits real factory commands over the real Client; it never touches
    /// table state.
    /// </summary>
    public sealed class AutoPlayer
    {
        private readonly ClientHandle _handle;
        private readonly string _name;
        private readonly CancellationToken _cancellation;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Task? _task;
        private int _actions;

        public AutoPlayer(ClientHandle handle, string name, CancellationToken cancellation)
        {
            _handle = handle;
            _name = name;
            _cancellation = cancellation;
        }

        public void Start()
        {
            _task = Task.Run(LoopAsync);
        }

        public async Task StopAsync()
        {
            _stop.Cancel();
            if (_task != null)
            {
                try
                {
                    await _task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        }

        private async Task LoopAsync()
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellation, _stop.Token);
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    PlayerSnapshot? snapshot = _handle.LatestPlayer;
                    WireDecisionView? decision = B1Actions.FindDecision(snapshot);
                    if (snapshot != null && decision != null)
                    {
                        WireLegalActionView? action = B1Actions.FindDiscard(decision) ?? B1Actions.FindPass(decision);
                        if (action != null)
                        {
                            string requestId = _name + "-" + Guid.NewGuid().ToString("N");
                            ProtocolCommand command = B1Actions.BuildCommand(
                                _handle,
                                snapshot,
                                decision,
                                action,
                                requestId);
                            await B1Actions.SubmitAndWaitAsync(
                                _handle,
                                command,
                                TimeSpan.FromSeconds(8),
                                linked.Token).ConfigureAwait(false);
                            _actions++;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // A transient deadline/response race is expected; the loop will
                    // observe the next decision. Persistent faults surface through
                    // the suite's positive assertions.
                }

                try
                {
                    await Task.Delay(50, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
