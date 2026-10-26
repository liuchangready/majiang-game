using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// Ticket admission through the REAL host and the REAL read-only validator, with a
    /// test-only ES256 signer emitting real frozen-contract bytes. Every refusal must be a
    /// WebSocket close 4401 (INVALID_TOKEN) with zero side effects on existing sessions;
    /// the precise rejection code is server-side audit only (redacted logging contract).
    /// </summary>
    public sealed class AdmissionTicketTests
    {
        private const int InvalidTokenClose = 4401;

        [Fact]
        public async Task ValidPlayerTicket_IsAdmittedWithFrozenFacts()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-a", "acct-1", 0);
            Welcome welcome = client.Welcome!;

            Assert.False(string.IsNullOrEmpty(welcome.SessionId));
            Assert.Equal("room-a", welcome.RoomId);
            Assert.Equal(Contracts.Wire.Role.Player, welcome.Role);
            Assert.Equal(0, welcome.SeatId);
            Assert.Equal((ulong)RoomHost.InitialOwnerEpochValue, welcome.OwnerEpoch);
            Assert.Equal(1u, welcome.BindingEpoch);
            Assert.Equal(ProtocolConstants.Major, welcome.ProtocolMajor);
            Assert.Equal(ProtocolConstants.Minor, welcome.ProtocolMinor);
            Assert.Equal(ProtocolConstants.RulesetVersion, welcome.RulesetVersion);
            Assert.Equal(ProtocolConstants.ProjectionVersion, welcome.ProjectionVersion);
            Assert.Equal("acct-1", welcome.Scope.AccountId);
            Assert.Equal(Contracts.Wire.Role.Player, welcome.Scope.Role);
            Assert.Equal("room-a", welcome.Scope.RoomId);
            Assert.False(string.IsNullOrEmpty(welcome.Scope.ScopeId));
            Assert.True(welcome.Scope.ExpiresAtMs > welcome.Scope.IssuedAtMs);
            Assert.Equal((uint)host.Options.MaxInboundMessageBytes, welcome.Limits.MaxInboundMessageBytes);
            Assert.Equal((uint)host.Options.HeartbeatIntervalMs, welcome.Limits.HeartbeatIntervalMs);
            Assert.True(welcome.Baseline.Cursor != null);
            await client.DisposeAsync();
        }

        [Fact]
        public async Task ValidSpectatorTicket_IsAdmittedWithoutSeat()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectSpectatorAsync("room-spec", "watcher-1");

            Assert.Equal(Contracts.Wire.Role.Spectator, client.Welcome!.Role);
            Assert.Equal(ProtocolConstants.NoSeat, client.Welcome.SeatId);
            await client.DisposeAsync();
        }

        [Fact]
        public async Task ForgedSignature_IsRefused4401()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            using TableTestSigner forger = TableTestKeyFactory.Create("test-key-1");
            // Same kid as the host key, different private key: the signature cannot verify.
            string ticket = forger.Sign(new TableTestTicket("acct-f", "room-a", TableAdmissionRole.Player, 0));
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await client.SendAsync(TableWire.Hello(ticket));
            await AssertCloseAsync(client, InvalidTokenClose, "INVALID_TOKEN");
        }

        [Fact]
        public async Task UnknownKid_IsRefused4401()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            using TableTestSigner stranger = TableTestKeyFactory.Create("ghost-key");
            string ticket = stranger.Sign(new TableTestTicket("acct-f", "room-a", TableAdmissionRole.Player, 0));
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await client.SendAsync(TableWire.Hello(ticket));
            await AssertCloseAsync(client, InvalidTokenClose, "INVALID_TOKEN");
        }

        [Fact]
        public async Task KidRotation_RefusedUntilKeyIsRegistered_ThenAdmitted()
        {
            using TableTestSigner key1 = TableTestKeyFactory.Create("rot-k1");
            using TableTestSigner key2 = TableTestKeyFactory.Create("rot-k2");
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(new[] { key1 });

            string ticket2 = key2.Sign(new TableTestTicket("acct-r", "room-a", TableAdmissionRole.Player, 0));
            TableTestClient stale = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await stale.SendAsync(TableWire.Hello(ticket2));
            await AssertCloseAsync(stale, InvalidTokenClose, "INVALID_TOKEN");

            await stale.DisposeAsync();
            await using TableServiceTestHost rotated = await TableServiceTestHost.StartAsync(
                new[] { key1, key2 });
            TableTestClient fresh = await TableTestClient.ConnectAsync(rotated.WsUrl + "?room=room-a");
            await fresh.AdmitAsync(key2.Sign(new TableTestTicket("acct-r", "room-a", TableAdmissionRole.Player, 0)));
            Assert.Equal(Contracts.Wire.Role.Player, fresh.Welcome!.Role);
            await fresh.DisposeAsync();
        }

        [Fact]
        public async Task WrongAudienceOrIssuer_IsRefused4401()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();

            TableTestClient wrongAud = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await wrongAud.SendAsync(TableWire.Hello(host.Signer.Sign(new TableTestTicket(
                "acct-a", "room-a", TableAdmissionRole.Player, 0, Audience: "mahjong-lobby-something-else"))));
            await AssertCloseAsync(wrongAud, InvalidTokenClose, "INVALID_TOKEN");
            await wrongAud.DisposeAsync();

            TableTestClient wrongIss = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await wrongIss.SendAsync(TableWire.Hello(host.Signer.Sign(new TableTestTicket(
                "acct-a", "room-a", TableAdmissionRole.Player, 0, Issuer: "evil-issuer"))));
            await AssertCloseAsync(wrongIss, InvalidTokenClose, "INVALID_TOKEN");
            await wrongIss.DisposeAsync();
        }

        [Fact]
        public async Task ExpiredNotYetValidAndOverTtlTickets_AreRefused4401()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (TableTestTicket bad in new[]
                     {
                         new TableTestTicket("acct-t", "room-a", TableAdmissionRole.Player, 0,
                             IssuedAtSeconds: now - 600, ExpiresAtSeconds: now - 30),
                         new TableTestTicket("acct-t", "room-a", TableAdmissionRole.Player, 0,
                             IssuedAtSeconds: now + 120, ExpiresAtSeconds: now + 300),
                         new TableTestTicket("acct-t", "room-a", TableAdmissionRole.Player, 0,
                             NotBeforeSeconds: now + 120),
                         new TableTestTicket("acct-t", "room-a", TableAdmissionRole.Player, 0, Ttl: TimeSpan.FromSeconds(400))
                     })
            {
                TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
                await client.SendAsync(TableWire.Hello(host.Signer.Sign(bad)));
                (int code, string reason, _) = await client.ExpectCloseAsync();
                Assert.Equal(InvalidTokenClose, code);
                Assert.Equal("INVALID_TOKEN", reason);
                await client.DisposeAsync();
            }
        }

        [Fact]
        public async Task ClaimContractViolations_AreRefused4401()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string kid = host.Signer.Kid;
            string basePayload(int seat, string role) =>
                "{\"iss\":\"mahjong-lobby\",\"sub\":\"acct-c\",\"aud\":\"mahjong-table\"," +
                "\"room_id\":\"room-a\",\"assignment_id\":\"asg-1\",\"role\":\"" + role + "\"," +
                "\"seat_id\":" + seat + ",\"jti\":\"jti-claim-" + seat + "\",\"iat\":" + now +
                ",\"exp\":" + (now + 240) + ",\"protocol_major\":1,\"ruleset_version\":1,\"projection_version\":1}";

            // role outside the frozen set
            TableTestClient badRole = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await badRole.SendAsync(TableWire.Hello(host.Signer.SignRaw(
                "{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\"" + kid + "\"}",
                basePayload(0, "ADMIN"))));
            await AssertCloseAsync(badRole, InvalidTokenClose, "INVALID_TOKEN");
            await badRole.DisposeAsync();

            // seat outside 0..3
            TableTestClient badSeat = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await badSeat.SendAsync(TableWire.Hello(host.Signer.SignRaw(
                "{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\"" + kid + "\"}",
                basePayload(7, "PLAYER"))));
            await AssertCloseAsync(badSeat, InvalidTokenClose, "INVALID_TOKEN");
            await badSeat.DisposeAsync();

            // spectator tickets must not carry a seat
            TableTestClient specWithSeat = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await specWithSeat.SendAsync(TableWire.Hello(host.Signer.SignRaw(
                "{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\"" + kid + "\"}",
                basePayload(2, "SPECTATOR"))));
            await AssertCloseAsync(specWithSeat, InvalidTokenClose, "INVALID_TOKEN");
            await specWithSeat.DisposeAsync();

            // unknown claim
            string unknown = basePayload(0, "PLAYER").TrimEnd('}') + ",\"scope\":\" smuggled\"}";
            TableTestClient unknownClaim = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await unknownClaim.SendAsync(TableWire.Hello(host.Signer.SignRaw(
                "{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\"" + kid + "\"}", unknown)));
            await AssertCloseAsync(unknownClaim, InvalidTokenClose, "INVALID_TOKEN");
            await unknownClaim.DisposeAsync();

            // duplicate claim
            string duplicate = basePayload(0, "PLAYER")[..1] + "\"sub\":\"other\"," + basePayload(0, "PLAYER")[1..];
            TableTestClient dupClaim = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await dupClaim.SendAsync(TableWire.Hello(host.Signer.SignRaw(
                "{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\"" + kid + "\"}", duplicate)));
            await AssertCloseAsync(dupClaim, InvalidTokenClose, "INVALID_TOKEN");
            await dupClaim.DisposeAsync();

            // non-canonical base64url (padded segments)
            TableTestClient padded = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await padded.SendAsync(TableWire.Hello(host.Signer.SignRaw(
                "{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\"" + kid + "\"}",
                basePayload(0, "PLAYER"), padSegments: true)));
            await AssertCloseAsync(padded, InvalidTokenClose, "INVALID_TOKEN");
            await padded.DisposeAsync();

            // ticket version claims outside the frozen matrix
            TableTestClient badVersion = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await badVersion.SendAsync(TableWire.Hello(host.Signer.Sign(new TableTestTicket(
                "acct-c", "room-a", TableAdmissionRole.Player, 0, ProtocolMajor: 2))));
            await AssertCloseAsync(badVersion, InvalidTokenClose, "INVALID_TOKEN");
            await badVersion.DisposeAsync();
        }

        [Fact]
        public async Task MalformedCompactTicket_IsRefused4401()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await client.SendAsync(TableWire.Hello("only-two-segments.here"));
            await AssertCloseAsync(client, InvalidTokenClose, "INVALID_TOKEN");
        }

        [Fact]
        public async Task TicketRoomMustMatchRouteRoom()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            string ticket = host.Signer.Sign(new TableTestTicket("acct-m", "room-real", TableAdmissionRole.Player, 0));
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-other");
            await client.SendAsync(TableWire.Hello(ticket));
            await AssertCloseAsync(client, InvalidTokenClose, "INVALID_TOKEN");
        }

        [Fact]
        public async Task JtiIsOneTime_ReplayIsRefused4401()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            string ticket = host.Signer.Sign(new TableTestTicket("acct-j", "room-a", TableAdmissionRole.Player, 0, Jti: "jti-replay-1"));

            TableTestClient first = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await first.AdmitAsync(ticket);
            await first.DisposeAsync();

            // Give the server teardown a moment, then replay the exact same ticket.
            await Task.Delay(500);
            TableTestClient second = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-a");
            await second.SendAsync(TableWire.Hello(ticket));
            await AssertCloseAsync(second, InvalidTokenClose, "INVALID_TOKEN");
        }

        [Fact]
        public async Task CrossAccountSeatTakeover_IsRefusedAndOriginalKeepsWorking()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient owner = await host.ConnectPlayerAsync("room-s", "acct-owner", 0);

            TableTestClient attacker = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-s");
            await attacker.SendAsync(TableWire.Hello(host.Signer.Sign(new TableTestTicket(
                "acct-attacker", "room-s", TableAdmissionRole.Player, 0))));
            await AssertCloseAsync(attacker, InvalidTokenClose, "INVALID_TOKEN");
            await attacker.DisposeAsync();

            // The refused admission had zero side effects: the owner session is still alive.
            string nonce = TableWire.RandomNonce();
            await owner.SendAsync(TableWire.HeartbeatPing(nonce));
            Envelope pong = await owner.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Heartbeat &&
                            envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong,
                TimeSpan.FromSeconds(10));
            Assert.Equal(nonce, pong.Heartbeat.Pong.Nonce);
            await owner.DisposeAsync();
        }

        [Fact]
        public async Task SpectatorCapacityIsEnforced()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                options => options.MaxSpectatorsPerRoom = 1);

            TableTestClient first = await host.ConnectSpectatorAsync("room-cap", "spec-1");
            TableTestClient second = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-cap");
            await second.SendAsync(TableWire.Hello(host.Signer.Sign(new TableTestTicket(
                "spec-2", "room-cap", TableAdmissionRole.Spectator, null))));
            await AssertCloseAsync(second, InvalidTokenClose, "INVALID_TOKEN");
            await second.DisposeAsync();

            string nonce = TableWire.RandomNonce();
            await first.SendAsync(TableWire.HeartbeatPing(nonce));
            Envelope pong = await first.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Heartbeat &&
                            envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong,
                TimeSpan.FromSeconds(10));
            Assert.Equal(nonce, pong.Heartbeat.Pong.Nonce);
            await first.DisposeAsync();
        }

        [Fact]
        public async Task ServiceRefusesToStartWithoutValidationKeys()
        {
            // The production entry point must exit with the dedicated code when no key source
            // is configured — there is no accept-all admission fallback anywhere.
            (int exit, _) = await RunServiceAsync(new[]
            {
                ("TABLESERVICE_TICKET_KEYS_FILE", ""),
                ("TABLESERVICE_TICKET_KEYS_PEM", ""),
                ("TABLESERVICE_TLS_PFX", "")
            });
            Assert.Equal(Program.ExitNoValidationKeys, exit);
        }

        [Fact]
        public async Task ServiceRefusesToStartWithInvalidOptions()
        {
            string assemblyDir = System.IO.Path.GetDirectoryName(typeof(TableServiceOptions).Assembly.Location)!;
            string keyFile = System.IO.Path.Combine(assemblyDir, "p51-test-key.pem");
            System.IO.File.WriteAllText(keyFile, TableTestKeyFactory.Create("k").PublicKeyPem);
            (int exit, _) = await RunServiceAsync(new[]
            {
                ("TABLESERVICE_TICKET_KEYS_FILE", keyFile),
                ("TABLESERVICE_TICKET_KEYS_PEM", ""),
                ("TABLESERVICE_TLS_PFX", ""),
                ("TABLESERVICE_HEARTBEAT_INTERVAL_MS", "100") // below the frozen 500ms floor
            });
            Assert.Equal(Program.ExitBadConfig, exit);
        }

        private static async Task AssertCloseAsync(TableTestClient client, int expectedCode, string expectedReason)
        {
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.True(code == expectedCode, "expected ws close " + expectedCode + " but got " + code);
            Assert.True(string.Equals(reason, expectedReason, StringComparison.Ordinal),
                "expected close reason " + expectedReason + " but got " + reason);
        }

        private static async Task<(int Exit, string Output)> RunServiceAsync((string Key, string Value)[] env)
        {
            string? assemblyDir = System.IO.Path.GetDirectoryName(typeof(TableServiceOptions).Assembly.Location);
            string dll = System.IO.Path.Combine(assemblyDir!, "Mahjong.TableService.dll");
            string exe = System.IO.Path.Combine(assemblyDir!, "Mahjong.TableService.exe");
            Assert.True(System.IO.File.Exists(dll), "service assembly not found: " + dll);

            // The real production entry point is the apphost; where the machine has no
            // system-wide runtime resolution (apphost cannot find hostfxr), fall back to the
            // same managed runtime the tests themselves are running under. Both paths run the
            // unmodified Program.Main with the real config validation.
            string fileName = exe;
            string arguments = string.Empty;
            string? runner = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(runner) &&
                string.Equals(System.IO.Path.GetFileName(runner), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                fileName = runner;
                arguments = "\"" + dll + "\"";
            }

            var start = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.Environment["TABLESERVICE_TICKET_KEYS_FILE"] = "";
            start.Environment["TABLESERVICE_TICKET_KEYS_PEM"] = "";
            foreach ((string key, string value) in env)
            {
                start.Environment[key] = value;
            }

            using Process process = Process.Start(start)!;
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, output + error);
        }
    }
}
