using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    /// <summary>
    /// Low-level real-WSS probe used only for authorization/TLS/admission
    /// negatives and exact-byte conflicts. It is NOT a fake service: every
    /// envelope goes over a real TLS WebSocket to the production TableService.
    /// It deliberately bypasses the production Client's role gate so the
    /// service-side refusal itself is exercised.
    /// </summary>
    public sealed class RawWssProbe : IAsyncDisposable
    {
        private readonly RealWssTableChannel _channel;

        public RawWssProbe(Uri endpoint, X509Certificate2 trustedRoot)
        {
            _channel = new RealWssTableChannel(endpoint, trustedRoot);
        }

        public Welcome? Welcome { get; private set; }

        public string? LastCloseCode { get; private set; }

        public async Task<Welcome> AdmitAsync(string ticket, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await _channel.OpenAsync(
                new TableChannelConnectRequest(ticket, "b1-raw-probe", null),
                timeoutCts.Token).ConfigureAwait(false);

            var hello = new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Hello,
                CorrelationId = "hello-b1-probe",
                BindingEpoch = 1,
                Hello = new Hello
                {
                    Ticket = ticket,
                    ProtocolMajor = ProtocolConstants.Major,
                    ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion,
                    ProjectionVersion = ProtocolConstants.ProjectionVersion,
                    ClientBuild = "b1-raw-probe",
                    ResumeRequested = false
                }
            };
            await _channel.SendAsync(WireCodec.Encode(hello), timeoutCts.Token).ConfigureAwait(false);
            Envelope response = await ReadRelevantAsync(timeoutCts.Token).ConfigureAwait(false);
            if (response.BodyCase == Envelope.BodyOneofCase.Welcome && response.Welcome != null)
            {
                Welcome = response.Welcome;
                return response.Welcome;
            }

            if (response.BodyCase == Envelope.BodyOneofCase.Close && response.Close != null)
            {
                LastCloseCode = response.Close.Code;
                throw new ProbeRejectedException(response.Close.Code ?? "CLOSE");
            }

            if (response.BodyCase == Envelope.BodyOneofCase.Error && response.Error != null)
            {
                LastCloseCode = response.Error.Code;
                throw new ProbeRejectedException(response.Error.Code ?? "ERROR");
            }

            throw new InvalidOperationException("Unexpected admission response " + response.BodyCase + ".");
        }

        public async Task<Envelope> SendCommandAsync(
            string requestId,
            global::Mahjong.Contracts.Wire.Command command,
            ulong controlEpoch,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (Welcome == null)
            {
                throw new InvalidOperationException("Probe is not admitted.");
            }

            var envelope = new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Command,
                RequestId = requestId,
                Scope = Welcome.Scope.Clone(),
                SessionId = Welcome.SessionId,
                RoomId = Welcome.RoomId,
                SeatId = Welcome.SeatId,
                BindingEpoch = Welcome.BindingEpoch,
                ControlEpoch = controlEpoch,
                OwnerEpoch = Welcome.OwnerEpoch,
                Command = command
            };
            return await SendAndReadAsync(envelope, timeout, cancellationToken).ConfigureAwait(false);
        }

        public async Task<Envelope> QueryReceiptAsync(
            string scopeId,
            string requestId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (Welcome == null)
            {
                throw new InvalidOperationException("Probe is not admitted.");
            }

            var envelope = new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major,
                ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion,
                ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.ReceiptQuery,
                CorrelationId = "query-b1-probe",
                SessionId = Welcome.SessionId,
                RoomId = Welcome.RoomId,
                SeatId = Welcome.SeatId,
                BindingEpoch = Welcome.BindingEpoch,
                ReceiptQuery = new ReceiptQuery
                {
                    RequestScopeId = scopeId,
                    RequestId = requestId
                }
            };
            return await SendAndReadAsync(envelope, timeout, cancellationToken).ConfigureAwait(false);
        }

        public async Task<Envelope> SendAndReadAsync(
            Envelope envelope,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await _channel.SendAsync(WireCodec.Encode(envelope), timeoutCts.Token).ConfigureAwait(false);
            return await ReadRelevantAsync(timeoutCts.Token).ConfigureAwait(false);
        }

        private async Task<Envelope> ReadRelevantAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                try
                {
                    ReadOnlyMemory<byte> frame = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                    WireDecodeResult decoded = WireCodec.Decode(frame.Span);
                    if (!decoded.IsOk || decoded.Envelope == null)
                    {
                        throw new InvalidOperationException("Probe received an undecodable frame.");
                    }

                    Envelope envelope = decoded.Envelope;
                    if (envelope.BodyCase == Envelope.BodyOneofCase.Heartbeat ||
                        envelope.BodyCase == Envelope.BodyOneofCase.PlayerUpdate ||
                        envelope.BodyCase == Envelope.BodyOneofCase.PublicUpdate ||
                        envelope.BodyCase == Envelope.BodyOneofCase.RoomUpdate)
                    {
                        continue;
                    }

                    return envelope;
                }
                catch (TableChannelClosedException ex)
                {
                    LastCloseCode = ex.Code;
                    throw new ProbeRejectedException(ex.Code);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _channel.CloseAsync(
                    new TableChannelCloseReason(ProtocolErrorCode.LocalClosed, permanent: true),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            _channel.Dispose();
        }

        public sealed class ProbeRejectedException : Exception
        {
            public ProbeRejectedException(string code) : base("The probe was rejected: " + code)
            {
                Code = code;
            }

            public string Code { get; }
        }
    }
}
