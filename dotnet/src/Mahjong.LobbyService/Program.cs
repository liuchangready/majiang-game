using System;
using System.Threading.Tasks;
using Mahjong.LobbyService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 64 * 1024;
});

LobbyOptions lobbyOptions = builder.Configuration.GetSection("Lobby").Get<LobbyOptions>() ?? new LobbyOptions();
lobbyOptions.Validate(builder.Environment.EnvironmentName);

TicketKeyRing keyRing = BuildKeyRing(lobbyOptions, builder.Environment);
var signer = new JwtTableTicketSigner(keyRing, lobbyOptions);
var clock = new SystemLobbyClock();

ILobbyStore store = lobbyOptions.UsesPostgres
    ? new PostgresLobbyStore(lobbyOptions.PostgresConnectionString!, lobbyOptions)
    : new InMemoryLobbyStore();

IRoomProjectionReader projections = lobbyOptions.UsesPostgres
    ? new PostgresRoomProjectionReader(lobbyOptions.PostgresConnectionString!, lobbyOptions.ProjectionSchema)
    : new EmptyRoomProjectionReader();

builder.Services.AddSingleton(lobbyOptions);
builder.Services.AddSingleton<ILobbyStore>(store);
builder.Services.AddSingleton<IRoomProjectionReader>(projections);
builder.Services.AddSingleton<ITableTicketSigner>(signer);
builder.Services.AddSingleton<ILobbyClock>(clock);
builder.Services.AddSingleton<LobbyApplication>();
builder.Services.AddHostedService<LobbyMaintenanceWorker>();

WebApplication app = builder.Build();
LobbyHttp.UseErrorHandling(app);
LobbyApplication lobby = app.Services.GetRequiredService<LobbyApplication>();
LobbyHttp.Map(app, lobby, signer, clock);

try
{
    await store.InitializeAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false);
    await app.RunAsync().ConfigureAwait(false);
}
finally
{
    await store.DisposeAsync().ConfigureAwait(false);
    if (projections is IAsyncDisposable disposableProjections)
    {
        await disposableProjections.DisposeAsync().ConfigureAwait(false);
    }
}

static TicketKeyRing BuildKeyRing(LobbyOptions options, IWebHostEnvironment environment)
{
    var ring = new TicketKeyRing();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    if (!string.IsNullOrWhiteSpace(options.TicketPrivateKeyPem))
    {
        ring.AddSigningKeyFromPem(options.TicketPrivateKeyPem!, options.TicketKeyId, now);
    }
    else if (string.Equals(environment.EnvironmentName, "Production", StringComparison.OrdinalIgnoreCase))
    {
        throw new LobbyOptionsException(
            "A production table-ticket signing key is required. Configure Lobby:TicketPrivateKeyPem or a secret-store equivalent.");
    }
    else
    {
        ring.AddEphemeralSigningKey(now);
    }

    if (options.AdditionalValidationPublicKeysPem != null)
    {
        foreach (string pem in options.AdditionalValidationPublicKeysPem)
        {
            if (!string.IsNullOrWhiteSpace(pem))
            {
                ring.AddValidationKeyFromPem(pem, null, now);
            }
        }
    }

    return ring;
}
