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

            // deviceId is a public hint. Possession of the high-entropy
            // bootstrap secret is what binds a request to the existing guest.
            string bootstrapSecret = LobbyText.RequireBootstrapSecret(command.BootstrapSecret);
            string bootstrapHash = LobbyIds.HashBootstrapSecret(bootstrapSecret);
            string displayName = LobbyText.RequireDisplayName(command.DisplayName);
            string deviceDigest = LobbyIds.HashDevice(deviceId);
            string bootstrapPrincipal = LobbyDigest.Compute(
                "guest.bootstrap.v1",
                deviceDigest,
                bootstrapHash);
            string payloadDigest = LobbyDigest.Compute(
                "auth.session.v2",
                deviceDigest,
                bootstrapHash,
                displayName);
            // The receipt principal includes the bootstrap hash, so knowing
            // only deviceId + requestId cannot replay a victim's bearer token.
            var key = new LobbyIdempotencyKey("bootstrap:" + bootstrapPrincipal, requestId);
            LobbyResponse? replay = await _store.TryReplayAsync(key, payloadDigest, cancellationToken)
                .ConfigureAwait(false);
            if (replay != null)
            {
                return replay;
            }

            DateTimeOffset now = _clock.UtcNow;
            string token = LobbyIds.NewToken();
            string tokenHash = LobbyIds.HashToken(token);

            return await _store.ExecuteIdempotentAsync(
                key,
                payloadDigest,
                now,
                async (tx, ct) =>
                {
                    LobbyAccount? account = await tx.FindAccountByBootstrapAsync(
                        deviceDigest,
                        bootstrapHash,
                        ct).ConfigureAwait(false);
                    if (account != null && account.ExpiresAt <= now)
                    {
                        throw new LobbyException(410, "GUEST_ACCOUNT_EXPIRED", "The guest account retention window has ended.");
                    }

                    if (account == null)
                    {
                        var candidate = new LobbyAccount(
                            LobbyIds.NewPrefixed("guest_"),
                            IsGuest: true,
                            displayName,
                            now,
                            now.AddDays(_options.GuestAccountRetentionDays));
                        await tx.InsertAccountAsync(candidate, deviceDigest, bootstrapHash, ct)
                            .ConfigureAwait(false);
                        // PostgreSQL uses ON CONFLICT DO NOTHING for a
                        // concurrent first bootstrap; always read the winner.
                        account = await tx.FindAccountByBootstrapAsync(deviceDigest, bootstrapHash, ct)
                                      .ConfigureAwait(false)
                                  ?? candidate;
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
