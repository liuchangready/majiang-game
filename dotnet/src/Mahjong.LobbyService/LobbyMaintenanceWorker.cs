using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mahjong.LobbyService
{
    /// <summary>
    /// Expires reservations/tickets and runs the temporary FIFO matchmaking
    /// pass. It is deliberately a bounded interval loop, not a per-table task.
    /// </summary>
    public sealed class LobbyMaintenanceWorker : BackgroundService
    {
        private readonly LobbyApplication _lobby;
        private readonly LobbyOptions _options;
        private readonly ILogger<LobbyMaintenanceWorker> _logger;

        public LobbyMaintenanceWorker(
            LobbyApplication lobby,
            LobbyOptions options,
            ILogger<LobbyMaintenanceWorker> logger)
        {
            _lobby = lobby;
            _options = options;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.MaintenanceIntervalSeconds));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _lobby.ExpireOnceAsync(stoppingToken).ConfigureAwait(false);
                    await _lobby.RunMatchmakingOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lobby maintenance pass failed; the next interval will retry.");
                }
            }
        }
    }
}
