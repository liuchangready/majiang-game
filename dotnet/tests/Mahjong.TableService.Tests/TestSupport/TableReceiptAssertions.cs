using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// B1-BLOCKER-01: every authoritative receipt reply must carry the server-issued scope
    /// that the request was fenced against. The assertion intentionally checks the full
    /// owner identity (not only the scope id) so a missing or shallow/echoed scope cannot
    /// pass: the production Client resolves by scope id and then checks the account.
    /// </summary>
    internal static class TableReceiptAssertions
    {
        public static void AssertAuthoritativeScope(Envelope receipt, Welcome welcome)
        {
            Assert.Equal(MessageType.Receipt, receipt.MessageType);
            Assert.NotNull(receipt.Scope);
            Assert.NotNull(welcome.Scope);
            Assert.Equal(welcome.Scope.ScopeId, receipt.Scope.ScopeId);
            Assert.Equal(welcome.Scope.AccountId, receipt.Scope.AccountId);
            Assert.Equal(welcome.Scope.Role, receipt.Scope.Role);
            Assert.Equal(welcome.Scope.RoomId, receipt.Scope.RoomId);
            Assert.Equal(welcome.Scope.IssuedAtMs, receipt.Scope.IssuedAtMs);
            Assert.Equal(welcome.Scope.ExpiresAtMs, receipt.Scope.ExpiresAtMs);
        }
    }
}
