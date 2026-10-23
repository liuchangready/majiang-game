using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Mahjong.LobbyService
{
    public sealed record SessionRequest(
        string? DeviceId,
        string? DisplayName,
        string? AuthMode,
        string? BootstrapSecret);

    public static class LobbyHttp
    {
        public static void UseErrorHandling(WebApplication app)
        {
            app.Use(async (context, next) =>
            {
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                try
                {
                    await next().ConfigureAwait(false);
                }
                catch (LobbyException ex)
                {
                    await WriteErrorAsync(context, ex.StatusCode, ex.Code, ex.Message).ConfigureAwait(false);
                }
                catch (TicketValidationException ex)
                {
                    await WriteErrorAsync(context, 401, ex.Code, ex.Message).ConfigureAwait(false);
                }
                catch (BadHttpRequestException ex)
                {
                    await WriteErrorAsync(context, ex.StatusCode, "MALFORMED_REQUEST", "The HTTP request is malformed.").ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    await WriteErrorAsync(context, 400, "MALFORMED_REQUEST", "The JSON request body is invalid.").ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    // Client disconnected; no response is possible or needed.
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "Unhandled lobby request failure. Path={Path}", context.Request.Path);
                    await WriteErrorAsync(context, 500, "INTERNAL_ERROR", "The lobby request failed.").ConfigureAwait(false);
                }
            });
        }

        public static void Map(
            WebApplication app,
            LobbyApplication lobby,
            ITableTicketSigner tickets,
            ILobbyClock clock)
        {
            app.MapGet("/v1/capabilities", async context =>
            {
                await WriteResponseAsync(context, await lobby.GetCapabilitiesAsync(context.RequestAborted)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            });

            app.MapGet("/.well-known/jwks.json", async context =>
            {
                JwksDocument document = tickets.GetJwks(clock.UtcNow);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.Headers["Cache-Control"] = "public, max-age=60";
                await context.Response.WriteAsync(LobbyJson.Serialize(document), context.RequestAborted)
                    .ConfigureAwait(false);
            });

            app.MapGet("/health/live", async context =>
            {
                await WriteResponseAsync(
                    context,
                    new LobbyResponse(200, LobbyJson.Serialize(new HealthView("live", "configured", true))))
                    .ConfigureAwait(false);
            });

            app.MapGet("/health/ready", async context =>
            {
                bool ready = await lobby.Store.CheckReadyAsync(context.RequestAborted).ConfigureAwait(false);
                var view = new HealthView(ready ? "ready" : "not_ready", "configured", ready);
                await WriteResponseAsync(
                    context,
                    new LobbyResponse(ready ? 200 : 503, LobbyJson.Serialize(view)))
                    .ConfigureAwait(false);
            });

            app.MapPost("/v1/auth/session", async context =>
            {
                string requestId = RequireIdempotency(context);
                SessionRequest request = await ReadJsonAsync<SessionRequest>(context).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(request.AuthMode) &&
                    !string.Equals(request.AuthMode, "GUEST", StringComparison.OrdinalIgnoreCase))
                {
                    throw new LobbyException(
                        501,
                        "OIDC_NOT_IMPLEMENTED",
                        "OIDC accounts are not implemented in P5.3; only restricted guest sessions are available.");
                }

                var command = new GuestSessionCommand(
                    request.DeviceId ?? string.Empty,
                    request.BootstrapSecret ?? string.Empty,
                    request.DisplayName ?? string.Empty);
                LobbyResponse response = await lobby.CreateGuestSessionAsync(
                    command,
                    requestId,
                    context.RequestAborted).ConfigureAwait(false);
                SetNoStore(context);
                await WriteResponseAsync(context, response).ConfigureAwait(false);
            });

            app.MapGet("/v1/auth/session", async context =>
            {
                LobbySessionInfo session = await AuthenticateAsync(lobby, context).ConfigureAwait(false);
                LobbyResponse response = lobby.GetCurrentSession(session);
                SetNoStore(context);
                await WriteResponseAsync(context, response).ConfigureAwait(false);
            });

            app.MapGet("/v1/rooms", async context =>
            {
                string? cursor = context.Request.Query["cursor"].ToString();
                if (string.IsNullOrEmpty(cursor))
                {
                    cursor = null;
                }

                int? limit = null;
                string? limitText = context.Request.Query["limit"].ToString();
                if (!string.IsNullOrEmpty(limitText))
                {
                    if (!int.TryParse(limitText, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out int parsed))
                    {
                        throw new LobbyException(400, "INVALID_PAGE_SIZE", "limit must be a positive integer.");
                    }

                    limit = parsed;
                }

                LobbyResponse response = await lobby.ListRoomsAsync(cursor, limit, context.RequestAborted)
                    .ConfigureAwait(false);
                await WriteResponseAsync(context, response).ConfigureAwait(false);
            });

            app.MapGet("/v1/rooms/{roomId}", async context =>
            {
                string roomId = (string)context.Request.RouteValues["roomId"]!;
                LobbyResponse response = await lobby.GetRoomAsync(roomId, context.RequestAborted)
                    .ConfigureAwait(false);
                await WriteResponseAsync(context, response).ConfigureAwait(false);
            });

            app.MapPost("/v1/rooms", async context =>
            {
                LobbySessionInfo session = await AuthenticateAsync(lobby, context).ConfigureAwait(false);
                string requestId = RequireIdempotency(context);
                CreateRoomCommand command = await ReadJsonAsync<CreateRoomCommand>(context).ConfigureAwait(false);
                LobbyResponse response = await lobby.CreateRoomAsync(
                    session.Account,
                    command,
                    requestId,
                    context.RequestAborted).ConfigureAwait(false);
                await WriteResponseAsync(context, response).ConfigureAwait(false);
            });

            app.MapPost("/v1/rooms/{roomId}/join", async context =>
            {
                LobbySessionInfo session = await AuthenticateAsync(lobby, context).ConfigureAwait(false);
                string requestId = RequireIdempotency(context);
                string roomId = (string)context.Request.RouteValues["roomId"]!;
                JoinRoomCommand command = await ReadOptionalJsonAsync<JoinRoomCommand>(context).ConfigureAwait(false)
                                           ?? new JoinRoomCommand(null);
                LobbyResponse response = await lobby.JoinRoomAsync(
                    session.Account,
                    roomId,
                    command,
                    requestId,
                    context.RequestAborted).ConfigureAwait(false);
                await WriteResponseAsync(context, response).ConfigureAwait(false);
            });

            app.MapPost("/v1/rooms/{roomId}/spectate", async context =>
            {
                LobbySessionInfo session = await AuthenticateAsync(lobby, context).ConfigureAwait(false);
                string requestId = RequireIdempotency(context);
                string roomId = (string)context.Request.RouteValues["roomId"]!;
                LobbyResponse response = await lobby.SpectateRoomAsync(
                    session.Account,
                    roomId,
                    requestId,
                    context.RequestAborted).ConfigureAwait(false);
                await WriteResponseAsync(context, response).ConfigureAwait(false);
            });

            app.MapPost("/v1/match/tickets", async context =>
            {
                LobbySessionInfo session = await AuthenticateAsync(lobby, context).ConfigureAwait(false);
                string requestId = RequireIdempotency(context);
                CreateMatchCommand command = await ReadJsonAsync<CreateMatchCommand>(context).ConfigureAwait(false);
                LobbyResponse response = await lobby.CreateMatchTicketAsync(
                    session.Account,
                    command,
                    requestId,
                    context.RequestAborted).ConfigureAwait(false);
                await WriteResponseAsync(context, response).ConfigureAwait(false);
            });

            app.MapGet("/v1/match/tickets/{matchTicketId}", async context =>
            {
                LobbySessionInfo session = await AuthenticateAsync(lobby, context).ConfigureAwait(false);
                string matchTicketId = (string)context.Request.RouteValues["matchTicketId"]!;
                LobbyResponse response = await lobby.GetMatchTicketAsync(
                    session.Account,
                    matchTicketId,
                    context.RequestAborted).ConfigureAwait(false);
                await WriteResponseAsync(context, response).ConfigureAwait(false);
            });

            app.MapPost("/v1/match/tickets/{matchTicketId}/cancel", async context =>
            {
                LobbySessionInfo session = await AuthenticateAsync(lobby, context).ConfigureAwait(false);
                string requestId = RequireIdempotency(context);
                string matchTicketId = (string)context.Request.RouteValues["matchTicketId"]!;
                LobbyResponse response = await lobby.CancelMatchTicketAsync(
                    session.Account,
                    matchTicketId,
                    requestId,
                    context.RequestAborted).ConfigureAwait(false);
                await WriteResponseAsync(context, response).ConfigureAwait(false);
            });

            app.MapPost("/v1/table-tickets", async context =>
            {
                LobbySessionInfo session = await AuthenticateAsync(lobby, context).ConfigureAwait(false);
                string requestId = RequireIdempotency(context);
                IssueTableTicketCommand command = await ReadJsonAsync<IssueTableTicketCommand>(context)
                    .ConfigureAwait(false);
                LobbyResponse response = await lobby.IssueTableTicketAsync(
                    session.Account,
                    command,
                    requestId,
                    context.RequestAborted).ConfigureAwait(false);
                SetNoStore(context);
                await WriteResponseAsync(context, response).ConfigureAwait(false);
            });
        }

        private static async Task<LobbySessionInfo> AuthenticateAsync(LobbyApplication lobby, HttpContext context)
        {
            string authorization = context.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                throw new LobbyException(401, "UNAUTHENTICATED", "A bearer lobby session is required.");
            }

            string token = authorization.Substring("Bearer ".Length).Trim();
            return await lobby.AuthenticateAsync(token, context.RequestAborted).ConfigureAwait(false);
        }

        private static string RequireIdempotency(HttpContext context)
        {
            return LobbyText.RequireRequestId(context.Request.Headers["Idempotency-Key"].ToString());
        }

        private static async Task<T> ReadJsonAsync<T>(HttpContext context) where T : class
        {
            if (!context.Request.HasJsonContentType())
            {
                throw new LobbyException(415, "UNSUPPORTED_MEDIA_TYPE", "A JSON request body is required.");
            }

            T? value;
            try
            {
                value = await context.Request.ReadFromJsonAsync<T>(LobbyJson.Options, context.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", "The JSON request body is invalid.");
            }

            if (value == null)
            {
                throw new LobbyException(400, "MALFORMED_REQUEST", "The JSON request body must be an object.");
            }

            return value;
        }

        private static async Task<T?> ReadOptionalJsonAsync<T>(HttpContext context) where T : class
        {
            if (context.Request.ContentLength == 0)
            {
                return null;
            }

            return await ReadJsonAsync<T>(context).ConfigureAwait(false);
        }

        private static async Task WriteResponseAsync(HttpContext context, LobbyResponse response)
        {
            if (context.Response.HasStarted)
            {
                return;
            }

            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            if (response.Replayed)
            {
                context.Response.Headers["Idempotent-Replayed"] = "true";
            }

            await context.Response.WriteAsync(response.Json, context.RequestAborted).ConfigureAwait(false);
        }

        private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message)
        {
            if (context.Response.HasStarted)
            {
                return;
            }

            context.Response.Clear();
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            SetNoStore(context);
            await context.Response.WriteAsync(
                LobbyJson.Serialize(new LobbyErrorView(code, message)),
                CancellationToken.None).ConfigureAwait(false);
        }

        private static void SetNoStore(HttpContext context)
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Pragma"] = "no-cache";
        }
    }
}
