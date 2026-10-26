using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;

namespace Mahjong.LocalHost.CrashWorker
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("usage: <saveDir> <roomId> <markerPath>");
                return 2;
            }

            string saveDir = args[0];
            string roomId = args[1];
            string marker = args[2];
            var options = new LocalTableHostOptions
            {
                SaveDirectory = saveDir,
                RoomId = roomId,
                OwnerInstanceId = "crash-owner"
            };

            ITableChannel channel = await LocalTableHost.CreateChannelAsync(options);
            string channelTicket = LocalTableHost.IssueTicket(options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "crash-worker", null), CancellationToken.None);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            string sessionTicket = LocalTableHost.IssueTicket(options);
            await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "crash-worker"), CancellationToken.None);
            for (int i = 0; i < 50 && session.State != TableConnectionState.Ready; i++)
            {
                await session.PumpOnceAsync(CancellationToken.None);
            }

            if (session.State != TableConnectionState.Ready)
            {
                Console.Error.WriteLine("worker not ready: " + session.LastError);
                return 3;
            }

            PlayerSnapshot baseline = session.PlayerSnapshot!;
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                baseline.RoundId,
                "local-direct",
                0UL,
                ProtocolActionType.DiscardTile,
                tileIds: new[] { baseline.PrivateView.ClosedTiles[0] });
            var command = new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId("crash-request-1"),
                session.Binding!.Scope.ScopeId,
                new StateVersion(baseline.StateVersion),
                new RoundId(baseline.RoundId),
                decisionId: null,
                controlEpoch: new ControlEpoch(0));
            CommandSubmission submission = await session.SubmitAsync(command, CancellationToken.None);
            if (!submission.Sent)
            {
                Console.Error.WriteLine("worker command not sent");
                return 4;
            }

            // Wait until the commit is observable; then die without any dispose/close path.
            for (int i = 0; i < 200 && session.PlayerSnapshot!.StateVersion == baseline.StateVersion; i++)
            {
                await Task.Delay(10);
                if (i % 2 == 0 && session.State == TableConnectionState.Ready)
                {
                    try
                    {
                        await session.PumpOnceAsync(CancellationToken.None);
                    }
                    catch
                    {
                        break;
                    }
                }
            }

            if (session.PlayerSnapshot!.StateVersion == baseline.StateVersion)
            {
                Console.Error.WriteLine("worker commit not observed");
                return 5;
            }

            File.WriteAllLines(marker, new[]
            {
                session.Binding.Scope.ScopeId.Value,
                "crash-request-1",
                session.PlayerSnapshot.StateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

            // Real crash: no actor close, no channel close, no store dispose.
            System.Diagnostics.Process.GetCurrentProcess().Kill();
            return 0;
        }    }
}
