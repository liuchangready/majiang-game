using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.LobbyService
{
    public sealed partial class LobbyApplication
    {
        public async Task<LobbyResponse> CreateGuestSessionAsync(
            GuestSessionCommand command,
            string requestId,
            CancellationToken cancellationToken)
        {
            if (command == null)
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", "A session request body is required.");
            }

            string deviceId = LobbyText.RequireDeviceId(command.DeviceId);
            if (deviceId.Length == 0)
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", "deviceId is required.");
            }

            string displayName = LobbyText.RequireDisplayName(command.DisplayName);
            string deviceDigest = LobbyIds.HashDevice(deviceId);
            string payloadDigest = LobbyDigest.Compute("auth.session.v1", deviceDigest, displayName);
            var key = new LobbyIdempotencyKey("device:" + deviceDigest, requestId);
            DateTimeOffset now = _clock.UtcNow;
            string token = LobbyIds.NewToken();
            string tokenHash = LobbyIds.HashToken(token);

            return await _store.ExecuteIdempotentAsync(
                key,
                payloadDigest,
                now,
                async (tx, ct) =>
                {
                    LobbyAccount? account = await tx.FindAccountByDeviceAsync(deviceDigest, ct).ConfigureAwait(false);
                    if (account != null && account.ExpiresAt <= now)
                    {
                        throw new LobbyException(410, "GUEST_ACCOUNT_EXPIRED", "The guest account retention window has ended.");
                    }

                    if (account == null)
                    {
                        account = new LobbyAccount(
                            LobbyIds.NewPrefixed("guest_"),
                            IsGuest: true,
                            displayName,
                            now,
                            now.AddDays(_options.GuestAccountRetentionDays));
                        await tx.InsertAccountAsync(account, deviceDigest, ct).ConfigureAwait(false);
                    }

                    DateTimeOffset sessionExpiry = now.AddHours(_options.SessionTtlHours);
                    if (sessionExpiry > account.ExpiresAt)
                    {
                        sessionExpiry = account.ExpiresAt;
                    }

                    var session = new LobbySession(tokenHash, account.AccountId, now, sessionExpiry, null);
                    await tx.InsertSessionAsync(session, ct).ConfigureAwait(false);

                    var view = new GuestSessionView(
                        account.AccountId,
                        account.IsGuest,
                        account.DisplayName,
                        token,
                        session.ExpiresAt,
                        Capabilities);
                    return new LobbyResponse(201, LobbyJson.Serialize(view));
                },
                cancellationToken).ConfigureAwait(false);
        }

        public LobbyResponse GetCurrentSession(LobbySessionInfo info)
        {
            if (info == null)
            {
                throw new ArgumentNullException(nameof(info));
            }

            var view = new CurrentSessionView(
                info.Account.AccountId,
                info.Account.IsGuest,
                info.Account.DisplayName,
                info.Account.ExpiresAt,
                info.Session.ExpiresAt,
                Capabilities);
            return new LobbyResponse(200, LobbyJson.Serialize(view));
        }
    }
}
