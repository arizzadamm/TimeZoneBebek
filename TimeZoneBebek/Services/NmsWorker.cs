using Microsoft.AspNetCore.SignalR;
using TimeZoneBebek.Hubs;

namespace TimeZoneBebek.Services
{
    public class NmsWorker : BackgroundService
    {
        private readonly ILogger<NmsWorker> _logger;
        private readonly IHubContext<NmsHub> _hubContext;
        private readonly NmsMonitorService _monitorService;
        private readonly NmsState _nmsState;

        public NmsWorker(ILogger<NmsWorker> logger, IHubContext<NmsHub> hubContext, NmsMonitorService monitorService, NmsState nmsState)
        {
            _logger = logger;
            _hubContext = hubContext;
            _monitorService = monitorService;
            _nmsState = nmsState;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await _monitorService.CollectAsync(stoppingToken);
                    _nmsState.SetLatest(snapshot);
                    await _hubContext.Clients.All.SendAsync("NmsStatusUpdated", snapshot, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[NMS] Failed to collect monitoring status");
                }

                var interval = Math.Max(_monitorService.GetRefreshIntervalSeconds(), 5);
                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
            }
        }
    }
}
