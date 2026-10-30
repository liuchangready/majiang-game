using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Google.Protobuf.Reflection;
using Mahjong.Contracts.Wire;
using Xunit;
using ProtocolConstants = Mahjong.Contracts.ProtocolConstants;

namespace Mahjong.Protocol.Tests
{
    public sealed class SchemaCompatibilityTests
    {
        private const string ExpectedSchemaFingerprint = "efcdb08d49fa430ac70cc1f61c1d6b9c13806bfb558e0f3be2639c3399123796";

        [Fact]
        public void EnvelopeFieldNumbersAreStable()
        {
            AssertFields(Envelope.Descriptor, new (int, string)[]
            {
                (1, "protocol_major"), (2, "protocol_minor"), (3, "ruleset_version"), (4, "projection_version"),
                (5, "message_type"), (6, "correlation_id"), (7, "request_id"), (8, "scope"), (9, "room_id"),
                (10, "session_id"), (11, "seat_id"), (12, "binding_epoch"), (13, "control_epoch"),
                (14, "owner_epoch"), (15, "cursor"), (16, "from_cursor"), (40, "hello"), (41, "welcome"),
                (42, "command"), (43, "receipt"), (44, "player_snapshot"), (45, "player_snapshot_query"),
                (46, "public_snapshot"), (47, "public_snapshot_query"), (48, "room_update"),
                (49, "sync_request"), (50, "sync_response"), (51, "receipt_query"), (52, "receipt_lookup"),
                (53, "heartbeat"), (54, "error"), (55, "close"), (56, "player_update"), (57, "public_update")
            });
        }

        [Fact]
        public void CommandReceiptCursorAndSyncFieldNumbersAreStable()
        {
            AssertFields(Command.Descriptor, new (int, string)[]
            {
                (1, "type"), (2, "canonical_payload"), (3, "payload_schema_version"),
                (4, "expected_state_version"), (5, "expected_round_id"), (6, "expected_decision_id")
            });
            AssertFields(Receipt.Descriptor, new (int, string)[]
            {
                (1, "status"), (2, "reason_code"), (3, "evaluated_state_version"), (4, "command_version"),
                (5, "event_seq"), (6, "committed_at_ms"), (7, "replayed"), (8, "canonical_payload_sha256")
            });
            AssertFields(StreamCursor.Descriptor, new (int, string)[]
            {
                (1, "stream_version"), (2, "projection_seq")
            });
            AssertFields(SyncRequest.Descriptor, new (int, string)[]
            {
                (1, "target"), (2, "known_cursor"), (3, "reason_code"), (4, "request_full_baseline")
            });
            AssertFields(SyncResponse.Descriptor, new (int, string)[]
            {
                (1, "target"), (2, "is_full_snapshot"), (3, "player"), (4, "public"), (5, "room"),
                (6, "state_version"), (7, "event_seq")
            });
            AssertFields(ReceiptQuery.Descriptor, new (int, string)[]
            {
                (1, "request_scope_id"), (2, "request_id")
            });
            AssertFields(ReceiptLookup.Descriptor, new (int, string)[]
            {
                (1, "status"), (2, "receipt"), (3, "canonical_payload_sha256")
            });
            AssertFields(Heartbeat.Descriptor, new (int, string)[]
            {
                (1, "ping"), (2, "pong")
            });
            AssertFields(ProtocolError.Descriptor, new (int, string)[]
            {
                (1, "code"), (2, "retryable"), (3, "field_path"), (4, "detail_code"), (5, "retry_after_ms")
            });
            AssertFields(Close.Descriptor, new (int, string)[]
            {
                (1, "code"), (2, "permanent"), (3, "websocket_close_code")
            });
        }

        [Fact]
        public void SnapshotFieldNumbersAreStable()
        {
            AssertFields(PlayerSnapshot.Descriptor, new (int, string)[]
            {
                (1, "session_id"), (2, "round_id"), (3, "cursor"), (4, "state_version"), (5, "event_seq"),
                (6, "public_view"), (7, "private_view"), (8, "session_version"), (9, "round_version")
            });
            AssertFields(PublicSnapshot.Descriptor, new (int, string)[]
            {
                (1, "session_id"), (2, "round_id"), (3, "cursor"), (4, "state_version"), (5, "event_seq"),
                (6, "view"), (7, "session_version"), (8, "round_version")
            });
            AssertFields(PublicBoardView.Descriptor, new (int, string)[]
            {
                (1, "phase"), (2, "round_wind"), (3, "round_number"), (4, "honba"), (5, "riichi_sticks"),
                (6, "dealer_seat"), (7, "current_seat"), (8, "wall_remaining"), (9, "dora_indicators"),
                (10, "scores"), (11, "players"), (12, "last_discard"), (13, "paused")
            });
            AssertFields(PublicMatchView.Descriptor, new (int, string)[]
            {
                (1, "board"), (2, "session"), (3, "clocks"), (4, "paused"), (5, "settlement_round_id"),
                (6, "settlement_events")
            });
            AssertFields(PlayerPrivateView.Descriptor, new (int, string)[]
            {
                (1, "seat"), (2, "closed_tiles"), (3, "drawn_tile_id"), (4, "legal_actions"),
                (5, "furiten"), (6, "discard_history"), (7, "decision"), (8, "cheat"),
                (16, "sleeve_tiles"), (17, "owner_melds"), (18, "control")
            });
            AssertFields(PlayerControlView.Descriptor, new (int, string)[]
            {
                (1, "control_epoch"), (2, "controlled_by_computer"), (3, "control_version")
            });
            AssertFields(PrivateCheatView.Descriptor, new (int, string)[]
            {
                (1, "cheat_points"), (2, "ledger_entries"), (3, "tile_mark"), (4, "sleeve_bloom"),
                (5, "beam_swap"), (6, "preparation"), (13, "off_turn_sleeve_bloom"),
                (14, "sleeve_bloom_reservation"), (15, "window_id")
            });
            AssertFields(PlayerPublicView.Descriptor, new (int, string)[]
            {
                (1, "seat"), (2, "hand_count"), (3, "melds"), (4, "discards"), (5, "riichi"),
                (13, "concealed_melds")
            });
            AssertFields(ConcealedMeldView.Descriptor, new (int, string)[]
            {
                (1, "type"), (2, "tile_count"), (3, "from_seat")
            });
            AssertFields(SleeveBloomView.Descriptor, new (int, string)[]
            {
                (1, "offer_id"), (2, "mode"), (3, "first_tile_id"), (4, "second_tile_id"),
                (5, "deadline_remaining_ms"), (6, "window_id"), (7, "reserved_tile_id"),
                (15, "created_turn_number"), (16, "draw_kind"), (17, "has_deadline")
            });
            AssertFields(SleeveBloomReservationView.Descriptor, new (int, string)[]
            {
                (1, "offer_id"), (2, "tile_id"), (3, "window_id")
            });
            AssertFields(TileMarkGroupView.Descriptor, new (int, string)[]
            {
                (1, "group_id"), (2, "tiles"), (8, "created_round_id"),
                (9, "created_round_ordinal"), (10, "expires_after_round_ordinal")
            });
            AssertFields(BeamSwapCandidateView.Descriptor, new (int, string)[]
            {
                (1, "slot"), (2, "tile_id"), (8, "slot_kind"), (9, "slot_index")
            });
            AssertFields(BeamSwapView.Descriptor, new (int, string)[]
            {
                (1, "offer_id"), (2, "candidates"), (3, "selectable_hand_tile_ids"),
                (4, "deadline_remaining_ms"), (5, "drawn_tile_id"), (13, "has_deadline")
            });
            AssertFields(RonWin.Descriptor, new (int, string)[]
            {
                (1, "turn_number"), (2, "winner"), (3, "loser"), (4, "win_tile_id"),
                (5, "yaku"), (6, "han"), (7, "fu"), (8, "score"), (9, "breakdown"),
                (16, "is_chankan")
            });
            AssertFields(SleeveBloomResolved.Descriptor, new (int, string)[]
            {
                (1, "offer_id"), (2, "kept_first"), (3, "result_tile_id"), (9, "owner_seat")
            });
            AssertFields(BeamSwapResolved.Descriptor, new (int, string)[]
            {
                (1, "offer_id"), (2, "action"), (3, "hand_tile_id"), (4, "target_slot"),
                (10, "owner_seat")
            });
            AssertFields(PublicUpdate.Descriptor, new (int, string)[]
            {
                (1, "snapshot"), (2, "from_cursor"), (3, "cursor"), (4, "is_full_snapshot"),
                (5, "visible_events")
            });
        }

        [Fact]
        public void EventFieldNumbersAndOneofAreStable()
        {
            AssertFields(EventEnvelope.Descriptor, new (int, string)[]
            {
                (1, "event_schema_version"), (2, "event_seq"), (3, "command_version"), (4, "event_ordinal"),
                (5, "event_type"), (6, "visibility"), (7, "unknown_payload"),
                (20, "stacking_started"), (21, "wall_built"), (22, "tile_dealt"), (23, "dealing_complete"),
                (24, "tile_drawn"), (25, "tile_discarded"), (26, "chi_declared"), (27, "pon_declared"),
                (28, "kan_declared"), (29, "rinshan_draw"), (30, "riichi_declared"), (31, "tsumo_win"),
                (32, "ron_win"), (33, "dora_revealed"), (34, "cheat_points_changed"), (35, "draw_game"),
                (36, "score_settled"), (37, "round_ended"), (38, "match_ended"),
                (39, "sleeve_bloom_resolved"), (40, "beam_swap_resolved")
            });

            OneofDescriptor body = Envelope.Descriptor.Oneofs.Single(o => o.Name == "body");
            Assert.Equal(18, body.Fields.Count);
            Assert.Equal(40, body.Fields[0].FieldNumber);
            Assert.Equal(57, body.Fields[body.Fields.Count - 1].FieldNumber);

            OneofDescriptor beat = Heartbeat.Descriptor.Oneofs.Single(o => o.Name == "beat");
            Assert.Equal(2, beat.Fields.Count);

            OneofDescriptor baseline = SyncResponse.Descriptor.Oneofs.Single(o => o.Name == "baseline");
            Assert.Equal(3, baseline.Fields.Count);

            OneofDescriptor eventOneof = EventEnvelope.Descriptor.Oneofs.Single(o => o.Name == "event");
            Assert.Equal(21, eventOneof.Fields.Count);
            Assert.Equal(20, eventOneof.Fields[0].FieldNumber);
            Assert.Equal(40, eventOneof.Fields[eventOneof.Fields.Count - 1].FieldNumber);

            AssertFields(PublicEventEnvelope.Descriptor, new (int, string)[]
            {
                (1, "event_schema_version"), (2, "event_seq"), (3, "command_version"), (4, "event_ordinal"),
                (5, "event_type"), (6, "visibility"), (7, "unknown_payload"),
                (20, "stacking_started"), (21, "wall_built"), (22, "dealing_complete"),
                (23, "tile_discarded"), (24, "chi_declared"), (25, "pon_declared"), (26, "kan_declared"),
                (27, "riichi_declared"), (28, "tsumo_win"), (29, "ron_win"), (30, "dora_revealed"),
                (31, "draw_game"), (32, "score_settled"), (33, "round_ended"), (34, "match_ended")
            });

            OneofDescriptor publicEvent = PublicEventEnvelope.Descriptor.Oneofs.Single(o => o.Name == "event");
            Assert.Equal(15, publicEvent.Fields.Count);
            Assert.Equal(20, publicEvent.Fields[0].FieldNumber);
            Assert.Equal(34, publicEvent.Fields[publicEvent.Fields.Count - 1].FieldNumber);
            Assert.Null(PublicEventEnvelope.Descriptor.FindFieldByName("tile_dealt"));
            Assert.Null(PublicEventEnvelope.Descriptor.FindFieldByName("tile_drawn"));
            Assert.Null(PublicEventEnvelope.Descriptor.FindFieldByName("rinshan_draw"));
            Assert.Null(PublicEventEnvelope.Descriptor.FindFieldByName("cheat_points_changed"));
            Assert.Null(PublicEventEnvelope.Descriptor.FindFieldByName("sleeve_bloom_resolved"));
            Assert.Null(PublicEventEnvelope.Descriptor.FindFieldByName("beam_swap_resolved"));
        }

        [Fact]
        public void ReservedRangesAndNamesAreFrozen()
        {
            Dictionary<string, SourceMessage> source = ProtoSource.Parse(AcquireProtoPath());

            AssertReserved(source["Envelope"], new[] { (17, 39), (58, 79) }, new[] { "wire_version", "business_version", "payload", "payload_type" });
            AssertReserved(source["Command"], new[] { (7, 15) }, new[] { "payload_sha256", "protobuf_bytes", "state_version", "event_seq" });
            AssertReserved(source["Receipt"], new[] { (9, 15) }, new[] { "state_version", "business_payload", "snapshot" });
            AssertReserved(source["Hello"], new[] { (9, 15) }, new[] { "wire_version", "business_version" });
            AssertReserved(source["Welcome"], new[] { (16, 24) }, Array.Empty<string>());
            AssertReserved(source["PublicSnapshot"], new[] { (9, 15) }, new[] { "cheat_points", "cheat_entries", "controls", "preparation", "tile_mark", "sleeve_bloom", "beam_swap" });
            AssertReserved(source["PlayerSnapshot"], new[] { (10, 16) }, new[] { "opponents_private", "all_hands", "seed" });
            AssertReserved(source["EventEnvelope"], new[] { (8, 19), (41, 59) }, new[] { "payload", "payload_type", "protobuf_bytes" });
            AssertReserved(source["PublicEventEnvelope"], new[] { (8, 19), (35, 59) }, new[] { "payload", "payload_type", "tile_dealt", "tile_drawn", "rinshan_draw", "cheat_points_changed" });
            AssertReserved(source["PublicBoardView"], new[] { (14, 20) }, new[] { "wall", "dead_wall", "seed", "ura_dora" });
            AssertReserved(source["PlayerPrivateView"], new[] { (9, 15) }, new[] { "other_seats_hands", "opponent_closed_tiles", "wall", "seed" });
            AssertReserved(source["PrivateCheatView"], new[] { (7, 12) }, new[] { "other_seats_cheat_points", "all_ledgers" });
            AssertReserved(source["PublicMatchView"], new[] { (7, 12) }, Array.Empty<string>());
            AssertReserved(source["SyncResponse"], new[] { (8, 14) }, Array.Empty<string>());
            AssertReserved(source["Heartbeat"], new[] { (3, 7) }, Array.Empty<string>());
            AssertReserved(source["ProtocolError"], new[] { (6, 12) }, Array.Empty<string>());
            AssertReserved(source["Close"], new[] { (4, 10) }, new[] { "reason", "token" });

            foreach (KeyValuePair<string, SourceMessage> pair in source)
            {
                MessageDescriptor? descriptor = DescriptorFor(pair.Key);
                if (descriptor == null)
                {
                    continue;
                }

                foreach ((int Start, int End) range in pair.Value.ReservedRanges)
                {
                    for (int number = range.Start; number <= range.End; number++)
                    {
                        Assert.Null(descriptor.FindFieldByNumber(number));
                    }
                }

                foreach (string reservedName in pair.Value.ReservedNames)
                {
                    Assert.Null(descriptor.FindFieldByName(reservedName));
                }
            }
        }

        [Fact]
        public void ProtoSourceAndRuntimeDescriptorAgreeOnEveryFieldAndType()
        {
            Dictionary<string, SourceMessage> source = ProtoSource.Parse(AcquireProtoPath());
            FileDescriptor file = Envelope.Descriptor.File;

            Assert.Equal(
                file.MessageTypes.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal),
                source.Keys.OrderBy(n => n, StringComparer.Ordinal));

            foreach (MessageDescriptor message in file.MessageTypes)
            {
                SourceMessage parsed = source[message.Name];
                Assert.Equal(parsed.Fields.Count, message.Fields.InDeclarationOrder().Count);

                foreach (SourceField field in parsed.Fields)
                {
                    FieldDescriptor actual = message.FindFieldByNumber(field.Number)
                        ?? throw new InvalidOperationException("Missing field " + message.Name + "." + field.Name);

                    Assert.Equal(field.Name, actual.Name);
                    Assert.Equal(field.Type, DescribeType(actual));
                    Assert.Equal(field.Repeated, actual.IsRepeated);
                    if (field.Optional)
                    {
                        Assert.True(actual.HasPresence, message.Name + "." + field.Name + " should preserve presence.");
                    }

                    if (field.Oneof != null)
                    {
                        Assert.Equal(field.Oneof, actual.ContainingOneof?.Name);
                    }
                }

                foreach (string oneof in parsed.Oneofs)
                {
                    Assert.Contains(message.Oneofs, o => o.Name == oneof);
                }
            }
        }

        [Fact]
        public void WholeSchemaFingerprintIsFrozen()
        {
            string actual = SchemaFingerprint.Compute(Envelope.Descriptor.File);
            Assert.Equal(ExpectedSchemaFingerprint, actual);
        }

        [Fact]
        public void ProtoHasNoMapAnyWellKnownTimeOrFloatingPointFields()
        {
            string text = File.ReadAllText(AcquireProtoPath());
            text = Regex.Replace(text, @"//.*$", string.Empty, RegexOptions.Multiline);

            Assert.DoesNotContain("map<", text);
            Assert.DoesNotContain("google.protobuf.Any", text);
            Assert.DoesNotContain("google.protobuf.Timestamp", text);
            Assert.DoesNotContain("google.protobuf.Duration", text);
            Assert.DoesNotMatch(@"\b(float|double|sfixed32|sfixed64)\b", text);
        }

        [Fact]
        public void ProtocGenerationIsRepeatable()
        {
            string protoPath = AcquireProtoPath();
            string protoDir = Path.GetDirectoryName(protoPath)!;
            string protoc = AcquireProtocPath();
            string root = Path.Combine(Path.GetTempPath(), "mahjong-p21-protoc-" + Guid.NewGuid().ToString("N"));

            try
            {
                string firstOut = Path.Combine(root, "first");
                string secondOut = Path.Combine(root, "second");
                Directory.CreateDirectory(firstOut);
                Directory.CreateDirectory(secondOut);
                string firstDescriptor = Path.Combine(root, "first.pb");
                string secondDescriptor = Path.Combine(root, "second.pb");

                RunProtoc(protoc, protoDir, firstOut, firstDescriptor);
                RunProtoc(protoc, protoDir, secondOut, secondDescriptor);

                byte[] firstCode = File.ReadAllBytes(Path.Combine(firstOut, "Mahjong.cs"));
                byte[] secondCode = File.ReadAllBytes(Path.Combine(secondOut, "Mahjong.cs"));
                byte[] firstSchema = File.ReadAllBytes(firstDescriptor);
                byte[] secondSchema = File.ReadAllBytes(secondDescriptor);

                Assert.True(firstCode.Length > 1000);
                Assert.Equal(firstCode, secondCode);
                Assert.Equal(firstSchema, secondSchema);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Fact]
        public void DomainDoesNotReferenceContractsWireProtobufUnityOrDatabase()
        {
            Assembly domain = typeof(Mahjong.Domain.TileId).Assembly;
            string[] references = domain.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

            Assert.DoesNotContain("Mahjong.Contracts", references);
            Assert.DoesNotContain("Mahjong.Contracts.Wire", references);
            Assert.DoesNotContain("Google.Protobuf", references);
            Assert.DoesNotContain("UnityEngine", references);
            Assert.DoesNotContain("System.Data", references);
        }

        [Fact]
        public void ContractsDoesNotReferenceWireOrProtobuf()
        {
            Assembly contracts = typeof(ProtocolConstants).Assembly;
            string[] references = contracts.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

            Assert.DoesNotContain("Mahjong.Contracts.Wire", references);
            Assert.DoesNotContain("Google.Protobuf", references);
        }

        [Fact]
        public void WireReferencesContractsAndProtobuf()
        {
            Assembly wire = typeof(WireCodec).Assembly;
            string[] references = wire.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

            Assert.Contains("Mahjong.Contracts", references);
            Assert.Contains("Google.Protobuf", references);
        }

        [Fact]
        public void EventSchemaFreezesTheV4NineteenPlusTwoV5PrivateEvents()
        {
            Assert.Equal(21, Enum.GetValues(typeof(EventType)).Length - 1);
            Assert.Equal((int)EventType.StackingStarted, 1);
            Assert.Equal((int)EventType.MatchEnded, 19);
            Assert.Equal((int)EventType.SleeveBloomResolved, 20);
            Assert.Equal((int)EventType.BeamSwapResolved, 21);
        }

        private static void AssertFields(MessageDescriptor descriptor, (int Number, string Name)[] expected)
        {
            Assert.Equal(expected.Length, descriptor.Fields.InDeclarationOrder().Count);
            foreach ((int number, string name) in expected)
            {
                FieldDescriptor? field = descriptor.FindFieldByNumber(number);
                Assert.NotNull(field);
                Assert.Equal(name, field!.Name);
            }
        }

        private static void AssertReserved(SourceMessage message, (int Start, int End)[] ranges, string[] names)
        {
            Assert.Equal(ranges.OrderBy(r => r.Start).ToArray(), message.ReservedRanges.OrderBy(r => r.Start).ToArray());
            Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToArray(), message.ReservedNames.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        private static MessageDescriptor? DescriptorFor(string name)
        {
            return Envelope.Descriptor.File.MessageTypes.FirstOrDefault(m => m.Name == name);
        }

        private static string DescribeType(FieldDescriptor field)
        {
            switch (field.FieldType)
            {
                case FieldType.Message:
                    return field.MessageType!.Name;
                case FieldType.Enum:
                    return field.EnumType!.Name;
                case FieldType.Double: return "double";
                case FieldType.Float: return "float";
                case FieldType.Int64: return "int64";
                case FieldType.UInt64: return "uint64";
                case FieldType.Int32: return "int32";
                case FieldType.Fixed64: return "fixed64";
                case FieldType.Fixed32: return "fixed32";
                case FieldType.Bool: return "bool";
                case FieldType.String: return "string";
                case FieldType.Group: return "group";
                case FieldType.Bytes: return "bytes";
                case FieldType.UInt32: return "uint32";
                case FieldType.SFixed32: return "sfixed32";
                case FieldType.SFixed64: return "sfixed64";
                case FieldType.SInt32: return "sint32";
                case FieldType.SInt64: return "sint64";
                default: return field.FieldType.ToString();
            }
        }

        private static string AcquireProtoPath()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "mahjong.proto");
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    "The test build did not copy mahjong.proto to " + AppContext.BaseDirectory + ".");
            }

            return path;
        }

        private static string AcquireProtocPath()
        {
            string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            string rid;
            string executable;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                rid = "windows_x64";
                executable = "protoc.exe";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                rid = "osx_x64";
                executable = "protoc";
            }
            else
            {
                rid = "linux_x64";
                executable = "protoc";
            }

            string path = Path.Combine(packages, "grpc.tools", "2.68.1", "tools", rid, executable);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    "protoc was not restored from Grpc.Tools 2.68.1 at " + path +
                    "; run dotnet restore for the test project.");
            }

            return path;
        }

        private static void RunProtoc(string protoc, string protoDir, string codeOut, string descriptorOut)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = protoc,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--proto_path=" + protoDir);
            startInfo.ArgumentList.Add("--csharp_out=" + codeOut);
            startInfo.ArgumentList.Add("--descriptor_set_out=" + descriptorOut);
            startInfo.ArgumentList.Add("mahjong.proto");

            using (Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start protoc."))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "protoc failed with exit code " + process.ExitCode + ": " + output + error);
                }
            }
        }
    }

    internal sealed class SourceMessage
    {
        public SourceMessage(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public List<SourceField> Fields { get; } = new List<SourceField>();

        public List<string> Oneofs { get; } = new List<string>();

        public List<(int Start, int End)> ReservedRanges { get; } = new List<(int, int)>();

        public List<string> ReservedNames { get; } = new List<string>();
    }

    internal sealed class SourceField
    {
        public SourceField(string name, int number, string type, bool repeated, bool optional, string? oneof)
        {
            Name = name;
            Number = number;
            Type = type;
            Repeated = repeated;
            Optional = optional;
            Oneof = oneof;
        }

        public string Name { get; }

        public int Number { get; }

        public string Type { get; }

        public bool Repeated { get; }

        public bool Optional { get; }

        public string? Oneof { get; }
    }

    internal static class ProtoSource
    {
        private static readonly Regex MessageRegex = new Regex(@"^message\s+([A-Za-z0-9_]+)\s*\{", RegexOptions.CultureInvariant);
        private static readonly Regex OneofRegex = new Regex(@"^oneof\s+([A-Za-z0-9_]+)\s*\{", RegexOptions.CultureInvariant);
        private static readonly Regex EnumRegex = new Regex(@"^enum\s+([A-Za-z0-9_]+)\s*\{", RegexOptions.CultureInvariant);
        private static readonly Regex FieldRegex = new Regex(
            @"^(repeated\s+|optional\s+)?([A-Za-z0-9_.]+)\s+([a-z][A-Za-z0-9_]*)\s*=\s*(\d+)\s*(?:\[[^\]]*\])?;",
            RegexOptions.CultureInvariant);
        private static readonly Regex ReservedRegex = new Regex(@"^reserved\s+(.+);$", RegexOptions.CultureInvariant);
        private static readonly Regex ReservedNameRegex = new Regex("\"([^\"]+)\"", RegexOptions.CultureInvariant);

        public static Dictionary<string, SourceMessage> Parse(string path)
        {
            var messages = new Dictionary<string, SourceMessage>(StringComparer.Ordinal);
            var blocks = new Stack<Block>();
            string[] lines = File.ReadAllLines(path);

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                Match messageMatch = MessageRegex.Match(line);
                if (messageMatch.Success)
                {
                    string name = messageMatch.Groups[1].Value;
                    var message = new SourceMessage(name);
                    messages.Add(name, message);
                    blocks.Push(new Block(BlockKind.Message, name, message));
                    continue;
                }

                Match enumMatch = EnumRegex.Match(line);
                if (enumMatch.Success)
                {
                    blocks.Push(new Block(BlockKind.Enum, enumMatch.Groups[1].Value, null));
                    continue;
                }

                Match oneofMatch = OneofRegex.Match(line);
                if (oneofMatch.Success)
                {
                    SourceMessage? owner = CurrentMessage(blocks);
                    if (owner == null)
                    {
                        throw new InvalidOperationException("oneof outside a message: " + line);
                    }

                    string name = oneofMatch.Groups[1].Value;
                    owner.Oneofs.Add(name);
                    blocks.Push(new Block(BlockKind.Oneof, name, owner));
                    continue;
                }

                if (line.StartsWith("}", StringComparison.Ordinal))
                {
                    if (blocks.Count == 0)
                    {
                        throw new InvalidOperationException("Unbalanced closing brace in proto.");
                    }

                    blocks.Pop();
                    continue;
                }

                SourceMessage? current = CurrentMessage(blocks);
                if (current == null)
                {
                    continue;
                }

                Match reservedMatch = ReservedRegex.Match(line);
                if (reservedMatch.Success)
                {
                    ParseReserved(reservedMatch.Groups[1].Value, current);
                    continue;
                }

                Match fieldMatch = FieldRegex.Match(line);
                if (fieldMatch.Success)
                {
                    bool repeated = fieldMatch.Groups[1].Value.StartsWith("repeated", StringComparison.Ordinal);
                    bool optional = fieldMatch.Groups[1].Value.StartsWith("optional", StringComparison.Ordinal);
                    string type = fieldMatch.Groups[2].Value;
                    string name = fieldMatch.Groups[3].Value;
                    int number = int.Parse(fieldMatch.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
                    string? oneof = CurrentOneof(blocks);
                    current.Fields.Add(new SourceField(name, number, type, repeated, optional, oneof));
                }
            }

            if (blocks.Count != 0)
            {
                throw new InvalidOperationException("Unbalanced braces in proto source.");
            }

            return messages;
        }

        private static SourceMessage? CurrentMessage(Stack<Block> blocks)
        {
            foreach (Block block in blocks)
            {
                if (block.Kind == BlockKind.Message)
                {
                    return block.Owner;
                }
            }

            return null;
        }

        private static string? CurrentOneof(Stack<Block> blocks)
        {
            foreach (Block block in blocks)
            {
                if (block.Kind == BlockKind.Oneof)
                {
                    return block.Name;
                }

                if (block.Kind == BlockKind.Message)
                {
                    return null;
                }
            }

            return null;
        }

        private static void ParseReserved(string body, SourceMessage message)
        {
            MatchCollection names = ReservedNameRegex.Matches(body);
            if (names.Count > 0)
            {
                for (int i = 0; i < names.Count; i++)
                {
                    message.ReservedNames.Add(names[i].Groups[1].Value);
                }

                return;
            }

            string[] parts = body.Split(',');
            foreach (string rawPart in parts)
            {
                string part = rawPart.Trim();
                int toIndex = part.IndexOf(" to ", StringComparison.Ordinal);
                if (toIndex >= 0)
                {
                    int start = int.Parse(part.Substring(0, toIndex).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    int end = int.Parse(part.Substring(toIndex + 4).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    message.ReservedRanges.Add((start, end));
                }
                else
                {
                    int number = int.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
                    message.ReservedRanges.Add((number, number));
                }
            }
        }

        private enum BlockKind
        {
            Message,
            Oneof,
            Enum
        }

        private sealed class Block
        {
            public Block(BlockKind kind, string name, SourceMessage? owner)
            {
                Kind = kind;
                Name = name;
                Owner = owner;
            }

            public BlockKind Kind { get; }

            public string Name { get; }

            public SourceMessage? Owner { get; }
        }
    }

    internal static class SchemaFingerprint
    {
        public static string Compute(FileDescriptor file)
        {
            var builder = new StringBuilder();
            foreach (EnumDescriptor enumDescriptor in file.EnumTypes.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                builder.Append("enum ").Append(enumDescriptor.Name).Append('\n');
                foreach (EnumValueDescriptor value in enumDescriptor.Values)
                {
                    builder.Append(value.Number).Append(':').Append(value.Name).Append('\n');
                }
            }

            foreach (MessageDescriptor message in file.MessageTypes.OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                builder.Append("message ").Append(message.Name).Append('\n');
                foreach (FieldDescriptor field in message.Fields.InDeclarationOrder().OrderBy(f => f.FieldNumber))
                {
                    builder.Append(field.FieldNumber).Append(':').Append(field.Name).Append(':')
                        .Append(field.FieldType).Append(':')
                        .Append(field.IsRepeated ? "repeated" : "singular").Append(':')
                        .Append(field.ContainingOneof?.Name ?? "-").Append('\n');
                }
            }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var hex = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    hex.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                }

                return hex.ToString();
            }
        }
    }
}
