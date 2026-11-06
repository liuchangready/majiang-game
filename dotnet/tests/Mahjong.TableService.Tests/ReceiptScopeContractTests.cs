using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;
using WireRequestScope = Mahjong.Contracts.Wire.RequestScope;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// B1-BLOCKER-01 supplementary coverage for receipt envelopes. The real Kestrel
    /// applied/rejected/replay/conflict/lookup matrix lives in SessionFlowTests and
    /// RuntimeDecisionRuntimeTests; this file pins the CONTROL receipt branch over a real
    /// socket (the wire-level control payload uses the frozen ControlRoundId/Expected
    /// ControlEpochV2 tags 20/21) plus the unreachable-today RECORDED status branch.
    /// </summary>
    public sealed class ReceiptScopeContractTests
    {
        [Fact]
        public async Task RealKestrel_ControlReceipt_CarriesAuthoritativeScope_NotForgedEnvelopeFields()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync(
                "room-control-scope", "acct-control-scope", 2);
            Welcome welcome = client.Welcome!;

            byte[] payload = CommandPayloadFactory
                .ClaimControl("round-control-scope", 0)
                .CopyBytes();
            const string RequestId = "req-control-scope";
            Envelope command = TableWire.Command(
                welcome, RequestId, Contracts.CommandKind.ClaimControl, payload);
            command.CorrelationId = "corr-control-scope";
            // The server fences the envelope by scope id only. Every other client-supplied
            // owner field is deliberately forged: the receipt must be rebuilt from the
            // server-issued admission identity, never echoed from this mutable object.
            command.Scope = new WireRequestScope
            {
                ScopeId = welcome.Scope.ScopeId,
                AccountId = "forged-account",
                Role = Role.Spectator,
                RoomId = "forged-room",
                IssuedAtMs = 1,
                ExpiresAtMs = 2
            };

            await client.SendAsync(command);
            Envelope receipt = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Receipt &&
                            envelope.RequestId == RequestId,
                TimeSpan.FromSeconds(20));

            Assert.Equal(RequestId, receipt.RequestId);
            Assert.Equal(command.CorrelationId, receipt.CorrelationId);
            Assert.NotNull(receipt.Receipt);
            Assert.True(
                receipt.Receipt.Status == Contracts.Wire.ReceiptStatus.Applied ||
                receipt.Receipt.Status == Contracts.Wire.ReceiptStatus.Rejected,
                "a control receipt must be an authoritative Applied/Rejected answer: " +
                receipt.Receipt.Status);
            Assert.False(string.IsNullOrEmpty(receipt.Receipt.ReasonCode));
            TableReceiptAssertions.AssertAuthoritativeScope(receipt, welcome);
            Assert.NotEqual("forged-account", receipt.Scope.AccountId);
            Assert.NotEqual("forged-room", receipt.Scope.RoomId);
            Assert.NotEqual(Role.Spectator, receipt.Scope.Role);

            await client.DisposeAsync();
        }

        [Fact]
        public void BuildReceipt_RecordedStatus_StillCarriesAuthoritativeScope()
        {
            // The current DispatchCommandAsync maps a control result to Applied/Rejected and
            // MapReceipt maps a durable RECORDED receipt to Applied, so a wire RECORDED is not
            // produced by this server today. This direct branch guard proves the status-agnostic
            // BuildReceipt path keeps the scope if that mapping ever starts emitting RECORDED;
            // it is NOT the end-to-end evidence (the real Kestrel control test above is).
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var identity = new TableConnectionIdentity(
                "session-recorded",
                "room-recorded",
                TableAdmissionRole.Player,
                2,
                "account-recorded",
                "scope-recorded",
                now.ToUnixTimeMilliseconds(),
                now.AddMinutes(2).ToUnixTimeMilliseconds(),
                "jti-recorded");

            var handler = (TableSessionHandler)RuntimeHelpers.GetUninitializedObject(typeof(TableSessionHandler));
            typeof(TableSessionHandler)
                .GetField("_identity", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(handler, identity);

            MethodInfo buildReceipt = typeof(TableSessionHandler).GetMethod(
                "BuildReceipt",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.NotNull(buildReceipt);

            var request = new Envelope
            {
                RequestId = "request-recorded",
                CorrelationId = "correlation-recorded"
            };
            var reply = (Envelope)buildReceipt.Invoke(
                handler,
                new object[]
                {
                    request,
                    new Contracts.Wire.Receipt
                    {
                        Status = Contracts.Wire.ReceiptStatus.Recorded,
                        ReasonCode = "LOCAL_CONTROL",
                        EvaluatedStateVersion = 7UL
                    }
                })!;

            Assert.Equal("request-recorded", reply.RequestId);
            Assert.Equal("correlation-recorded", reply.CorrelationId);
            Assert.Equal(Contracts.Wire.ReceiptStatus.Recorded, reply.Receipt.Status);
            Assert.Equal(7UL, reply.Receipt.EvaluatedStateVersion);
            TableReceiptAssertions.AssertAuthoritativeScope(
                reply,
                new Welcome
                {
                    Scope = new WireRequestScope
                    {
                        ScopeId = identity.ScopeId,
                        AccountId = identity.AccountId,
                        Role = Role.Player,
                        RoomId = identity.RoomId,
                        IssuedAtMs = (ulong)identity.ScopeIssuedAtMs,
                        ExpiresAtMs = (ulong)identity.ScopeExpiresAtMs
                    }
                });
        }
    }
}
