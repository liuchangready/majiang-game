using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.TableRuntime;
using Mahjong.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mahjong.TableService
{
    /// <summary>
    /// Composes the /v1/table WebSocket host: per-connection session handlers, health
    /// endpoints and the token-gated operator drain endpoint. The host never registers
    /// ForwardedHeaders middleware — an X-Forwarded-* header from an untrusted hop is never
    /// consulted, so remote addresses and schemes stay the Kestrel-observed truth; a controlled
    /// ingress that terminates TLS is trusted because the operator bound it, not because a
    /// header said so.
    /// </summary>
    public static class TableServiceWebHost
    {
        public const string TablePath = "/v1/table";

        public const string LivePath = "/health/live";

        public const string ReadyPath = "/health/ready";

        public const string DrainPath = "/admin/drain";

        /// <summary>Room-id charset mirrored from ProtocolText.RequireId (frozen id contract).</summary>
        public static bool IsValidRoomId(string? roomId)
        {
            if (string.IsNullOrEmpty(roomId) || roomId.Length > ProtocolConstants.MaxIdChars)
            {
                return false;
            }

            if (char.IsWhiteSpace(roomId[0]) || char.IsWhiteSpace(roomId[roomId.Length - 1]))
            {
                return false;
            }

            foreach (char c in roomId)
            {
                bool valid =
                    (c >= 'a' && c <= 'z') ||
                    (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') ||
                    c == '_' || c == '-' || c == '.' || c == ':';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Builds (but does not start) the WebApplication. Loopback-only by default; tests start
        /// it on an OS-assigned port (port 0) and read the actual port from
        /// <see cref="TableServiceHostRuntime.GetBoundPort"/>. The room runtime source defaults
        /// to the production cryptographically-random initialization; a test or controlled
        /// deployment may inject its own <see cref="ITableRoundSource"/> — a registry built
        /// without any source stays fail-closed and never serves a fabricated table.
        /// </summary>
        public static TableServiceHostRuntime Build(
            TableServiceOptions options,
            ITableTicketKeyProvider ticketKeys,
            IPAddress? bindAddress = null,
            int bindPort = 0,
            ITableRoundSource? roundSource = null)
        {
            options.Validate();

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Information);
            builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                IPAddress address = bindAddress ?? IPAddress.Loopback;
                kestrel.Listen(address, bindPort, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                    if (options.ServerCertificate != null)
                    {
                        listen.UseHttps(options.ServerCertificate);
                    }
                });
                kestrel.Limits.MaxRequestBodySize = null;
            });

            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(ticketKeys);
            builder.Services.AddSingleton<TableTicketValidator>();
            builder.Services.AddSingleton<DrainState>();
            builder.Services.AddSingleton<ITicketConsumptionRegistry>(_ =>
                new InMemoryTicketConsumptionRegistry(options));
            builder.Services.AddSingleton<ITableSessionDirectory, InMemorySessionDirectory>();
            builder.Services.AddSingleton<ITableStreamContractCodec, WireTableStreamContractCodec>();
            builder.Services.AddSingleton<ITableClock>(SystemTableClock.Instance);
            builder.Services.AddSingleton<ITableRoundSource>(roundSource ?? CryptographicTableRoundSource.Instance);

            // P5.4b-A durable host assembly. Postgres mode binds the SAME P5.2 store to the
            // actors' durable context and the host's scope issuance — and refuses to build
            // (before any listener exists) when the configuration, key or schema is wrong.
            // There is no fallback to the in-memory registry in this mode, ever.
            ITableHostRegistry hostRegistry;
            IAsyncDisposable? storeOwner = null;
            if (options.StoreMode == TableHostStoreMode.Postgres)
            {
                var pgOptions = new Mahjong.Infrastructure.PostgresTableOptions(
                    options.PostgresConnectionString!,
                    options.PostgresPayloadKeyBase64 ?? string.Empty,
                    options.PostgresCommandTimeoutSeconds,
                    options.PostgresMaxPoolSize);
                if (options.PostgresApplyMigrations)
                {
                    Mahjong.Infrastructure.PostgresTableMigrator
                        .ApplyAsync(options.PostgresConnectionString!)
                        .GetAwaiter()
                        .GetResult();
                }

                if (!IsStoreSchemaReady(options.PostgresConnectionString!))
                {
                    throw new TableServiceCapacityException(
                        "PERSISTENT_STORE_NOT_READY",
                        "The table_store schema is not ready; a persistent host refuses to serve.");
                }

                var pgStore = new Mahjong.Infrastructure.PostgresTableStore(pgOptions);
                storeOwner = pgStore;
                var scopeAuthority = new PostgresTableHostScopeAuthority(pgStore);
                hostRegistry = new PostgresTableHostRegistry(
                    options,
                    new WireTableStreamContractCodec(),
                    SystemTableClock.Instance,
                    roundSource ?? CryptographicTableRoundSource.Instance,
                    pgStore,
                    scopeAuthority,
                    // The lease contract lets an owner RECLAIM its own live lease when the
                    // owner_instance matches (restart-reclaim semantics), so this id must be
                    // unique per host RUNTIME — not per process. A process id collides for
                    // every embedding that hosts two registries side by side (the dual-host
                    // fencing scenario): the second runtime would treat a foreign live lease
                    // as its own, take the room over and welcome admissions it must refuse.
                    // The runner only pins the tablesvc- prefix.
                    "tablesvc-" + Guid.NewGuid().ToString("N"));
            }
            else
            {
                hostRegistry = new InMemoryTableHostRegistry(
                    options,
                    new WireTableStreamContractCodec(),
                    SystemTableClock.Instance,
                    roundSource ?? CryptographicTableRoundSource.Instance);
            }

            builder.Services.AddSingleton<ITableHostRegistry>(hostRegistry);
            builder.Services.AddHostedService<TableTickDriver>();

            WebApplication app = builder.Build();

            // Deliberately NO UseForwardedHeaders: see the class summary.
            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromMilliseconds(Math.Max(250, options.HeartbeatIntervalMs / 2))
            });

            var connectionCounter = 0L;

            app.MapGet(TablePath, async (HttpContext context) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                    context.Response.Headers.Upgrade = "websocket";
                    context.Response.Headers.Connection = "Upgrade";
                    return;
                }

                if (!context.Request.Query.TryGetValue("room", out Microsoft.Extensions.Primitives.StringValues rooms) ||
                    rooms.Count != 1 ||
                    !IsValidRoomId(rooms[0]))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                string admissionRoom = rooms[0]!;

                DrainState drain = context.RequestServices.GetRequiredService<DrainState>();
                if (drain.IsDraining)
                {
                    // Refuse the handshake itself so no half-admitted session exists.
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers.RetryAfter = "5";
                    return;
                }

                TableTicketValidator validator = context.RequestServices.GetRequiredService<TableTicketValidator>();
                ITicketConsumptionRegistry consumption =
                    context.RequestServices.GetRequiredService<ITicketConsumptionRegistry>();
                ITableSessionDirectory sessions =
                    context.RequestServices.GetRequiredService<ITableSessionDirectory>();
                ITableHostRegistry hosts = context.RequestServices.GetRequiredService<ITableHostRegistry>();
                ILogger<TableSessionHandler> logger =
                    context.RequestServices.GetRequiredService<ILogger<TableSessionHandler>>();

                long connectionEpoch = Interlocked.Increment(ref connectionCounter);
                var handler = new TableSessionHandler(
                    await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false),
                    admissionRoom,
                    options,
                    drain,
                    validator,
                    consumption,
                    sessions,
                    hosts,
                    logger,
                    connectionEpoch);
                await handler.RunAsync(context.RequestAborted).ConfigureAwait(false);
            });

            app.MapGet(LivePath, (HttpContext context) =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            });

            app.MapGet(ReadyPath, async (HttpContext context) =>
            {
                // Readiness reflects the REAL serving state (D-1-10): the drain gate OR a
                // process whose every KNOWN room is fenced off (lease lost / faulted / closed)
                // is unhealthy — and the authoritative gate is each room's OWN actor
                // lifecycle snapshot, not a ticker-polled bool. A room registry with NO room
                // yet is healthy: its runtime source (CSPRNG wall + Domain round) and its
                // room capacity are ready to admit the first table, and readiness must never
                // force an operator to bypass the health surface just to serve room #1.
                DrainState drain = context.RequestServices.GetRequiredService<DrainState>();
                ITableHostRegistry hosts = context.RequestServices.GetRequiredService<ITableHostRegistry>();
                bool canServe;
                if (drain.IsDraining)
                {
                    canServe = false;
                }
                else
                {
                    // D-1-10 over ANY registry implementation (memory and PostgreSQL alike):
                    // the authoritative gate is each known room's OWN actor lifecycle. A room
                    // that exists durably but could not be served by this process (failed
                    // cold recovery, foreign live lease) also counts as known-and-not-
                    // serving, so a persistent host with an unrecoverable room reports 503
                    // instead of a green light over a room it refuses to admit.
                    bool anyKnownRoom = false;
                    bool anyServingRoom = false;
                    if (hosts is PostgresTableHostRegistry postgres &&
                        postgres.FailedRooms.Count > 0)
                    {
                        anyKnownRoom = true;
                    }

                    foreach (RoomHost room in hosts.Hosts)
                    {
                        anyKnownRoom = true;
                        try
                        {
                            using var read = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            TableSnapshot snapshot = await room.Actor
                                .GetSnapshotAsync(read.Token)
                                .ConfigureAwait(false);
                            if (snapshot.Lifecycle == TableActorLifecycle.Serving)
                            {
                                anyServingRoom = true;
                            }
                        }
                        catch (Exception)
                        {
                            // An unreachable actor is definitively not serving.
                        }
                    }

                    canServe = !anyKnownRoom || anyServingRoom;
                }

                context.Response.StatusCode = canServe
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status503ServiceUnavailable;
            });

            app.MapPost(DrainPath, async (HttpContext context) =>
            {
                TableServiceOptions configured = context.RequestServices.GetRequiredService<TableServiceOptions>();
                if (string.IsNullOrEmpty(configured.AdminDrainToken))
                {
                    // Endpoint does not exist until an operator token is configured.
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                string? presented = context.Request.Headers["X-Admin-Token"];
                if (!IsConstantTimeTokenMatch(presented, configured.AdminDrainToken))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                bool enable = true;
                if (context.Request.Query.TryGetValue("enabled", out Microsoft.Extensions.Primitives.StringValues values) &&
                    values.Count == 1)
                {
                    if (!bool.TryParse(values[0], out enable))
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        return;
                    }
                }

                context.RequestServices.GetRequiredService<DrainState>().Set(enable);
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsync(enable ? "draining" : "serving", context.RequestAborted)
                    .ConfigureAwait(false);
            });

            return new TableServiceHostRuntime(app, options, hostRegistry, storeOwner);
        }

        /// <summary>
        /// Constant-time admin-token comparison: the presented string and the configured
        /// secret are digested with the same mechanism, every comparison reads the same number
        /// of bytes in the same class (D-1-11) — no early length exit, no early first-unequal
        /// byte leak through timing.
        /// </summary>
        private static bool IsConstantTimeTokenMatch(string? presented, string configured)
        {
            if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(configured))
            {
                return false;
            }

            Span<byte> left = stackalloc byte[System.Security.Cryptography.SHA256.HashSizeInBytes];
            Span<byte> right = stackalloc byte[System.Security.Cryptography.SHA256.HashSizeInBytes];
            bool hashedLeft = System.Security.Cryptography.SHA256.TryHashData(
                System.Text.Encoding.UTF8.GetBytes(presented), left, out _);
            bool hashedRight = System.Security.Cryptography.SHA256.TryHashData(
                System.Text.Encoding.UTF8.GetBytes(configured), right, out _);
            return hashedLeft && hashedRight &&
                   System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
        }

        /// <summary>
        /// Proves the <c>table_store</c> schema is live before any listener exists. Any failure
        /// to PROVE readiness — unreachable database, missing schema, missing migration row —
        /// is "not ready": a persistent host refuses to serve rather than guessing.
        /// </summary>
        private static bool IsStoreSchemaReady(string connectionString)
        {
            try
            {
                return Mahjong.Infrastructure.PostgresTableMigrator
                    .IsReadyAsync(connectionString)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    "tableservice: persistent store readiness could not be proven: " +
                    exception.GetType().Name);
                return false;
            }
        }
    }

    /// <summary>
    /// Started-host handle: the bound loopback port and graceful stop. Shutdown order is
    /// strict: listeners stop first, then every OWNED table actor is disposed (D-1-05),
    /// and only then — with no actor left that could touch it — the process-wide durable
    /// store (PostgreSQL pool) is disposed in persistent mode.
    /// </summary>
    public sealed class TableServiceHostRuntime
    {
        private readonly WebApplication _app;
        private readonly ITableHostRegistry? _hosts;
        private readonly IAsyncDisposable? _storeOwner;

        public TableServiceHostRuntime(
            WebApplication app,
            TableServiceOptions options,
            ITableHostRegistry? hosts = null,
            IAsyncDisposable? storeOwner = null)
        {
            _app = app;
            Options = options;
            _hosts = hosts;
            _storeOwner = storeOwner;
        }

        public TableServiceOptions Options { get; }

        public IServiceProvider Services => _app.Services;

        public ITableHostRegistry? HostRegistry => _hosts;

        public string BoundAddress { get; private set; } = string.Empty;

        public int GetBoundPort()
        {
            // Kestrel reports the effective addresses only after start; read them from the
            // server's address feature instead of guessing.
            IServer? server = _app.Services.GetService<IServer>();
            IServerAddressesFeature? addresses = server?.Features.Get<IServerAddressesFeature>();
            foreach (string address in addresses?.Addresses ?? (ICollection<string>)Array.Empty<string>())
            {
                int lastColon = address.LastIndexOf(':');
                if (lastColon >= 0 &&
                    int.TryParse(address.AsSpan(lastColon + 1), out int port))
                {
                    return port;
                }
            }

            throw new InvalidOperationException("The host has no bound address yet (start it first).");
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _app.StartAsync(cancellationToken).ConfigureAwait(false);
            IServer? server = _app.Services.GetService<IServer>();
            IServerAddressesFeature? addresses = server?.Features.Get<IServerAddressesFeature>();
            BoundAddress = string.Join(
                ";",
                addresses?.Addresses ?? (IEnumerable<string>)Array.Empty<string>());
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
            if (_hosts is InMemoryTableHostRegistry memoryHosts)
            {
                // Shutdown disposes every OWNED table actor: no pump, stream or timer of a
                // table actor outlives the host (D-1-05).
                await memoryHosts.DisposeHostsAsync().ConfigureAwait(false);
            }

            if (_hosts is PostgresTableHostRegistry postgresHosts)
            {
                await postgresHosts.DisposeHostsAsync().ConfigureAwait(false);
            }

            if (_storeOwner != null)
            {
                // Last: the process-wide durable store. Every actor and stream is already
                // gone, so nothing can issue database work through a disposed pool.
                await _storeOwner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
