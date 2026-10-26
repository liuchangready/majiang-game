using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.TableRuntime;
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
        /// <see cref="TableServiceHostRuntime.GetBoundPort"/>.
        /// </summary>
        public static TableServiceHostRuntime Build(
            TableServiceOptions options,
            ITableTicketKeyProvider ticketKeys,
            IPAddress? bindAddress = null,
            int bindPort = 0)
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
            builder.Services.AddSingleton<ITableHostRegistry>(sp => new InMemoryTableHostRegistry(
                options,
                sp.GetRequiredService<ITableStreamContractCodec>(),
                sp.GetRequiredService<ITableClock>()));
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

            app.MapGet(ReadyPath, (HttpContext context) =>
            {
                DrainState drain = context.RequestServices.GetRequiredService<DrainState>();
                context.Response.StatusCode = drain.IsDraining
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status200OK;
                return Task.CompletedTask;
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
                if (!string.Equals(presented, configured.AdminDrainToken, StringComparison.Ordinal))
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

            return new TableServiceHostRuntime(app, options);
        }
    }

    /// <summary>Started-host handle: the bound loopback port and graceful stop.</summary>
    public sealed class TableServiceHostRuntime
    {
        private readonly WebApplication _app;

        public TableServiceHostRuntime(WebApplication app, TableServiceOptions options)
        {
            _app = app;
            Options = options;
        }

        public TableServiceOptions Options { get; }

        public IServiceProvider Services => _app.Services;

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
        }
    }
}
