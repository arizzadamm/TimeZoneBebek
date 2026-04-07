using Microsoft.AspNetCore.Mvc;
using TimeZoneBebek.Models;
using TimeZoneBebek.Services;

namespace TimeZoneBebek.Controllers
{
    [ApiController]
    [Route("api/nms")]
    public class NmsController : ControllerBase
    {
        private readonly NmsMonitorService _monitorService;
        private readonly NmsState _nmsState;

        public NmsController(NmsMonitorService monitorService, NmsState nmsState)
        {
            _monitorService = monitorService;
            _nmsState = nmsState;
        }

        [HttpGet("status")]
        public async Task<IActionResult> GetStatus()
        {
            var snapshot = _nmsState.GetLatest();
            if (snapshot == null)
            {
                snapshot = await _monitorService.CollectAsync(HttpContext.RequestAborted);
                _nmsState.SetLatest(snapshot);
            }

            return Ok(snapshot);
        }

        [HttpGet("history")]
        public async Task<IActionResult> GetHistory([FromQuery] string? range = "5m")
        {
            var snapshot = _nmsState.GetLatest();
            if (snapshot == null)
            {
                snapshot = await _monitorService.CollectAsync(HttpContext.RequestAborted);
                _nmsState.SetLatest(snapshot);
            }

            var resolvedRange = ResolveRange(range);
            var points = _nmsState.GetHistory(resolvedRange);

            return Ok(new NmsHistoryResponse
            {
                Range = NormalizeRange(range),
                GeneratedAtUtc = DateTime.UtcNow,
                Points = points.ToList()
            });
        }

        private static TimeSpan ResolveRange(string? range)
        {
            return NormalizeRange(range) switch
            {
                "30m" => TimeSpan.FromMinutes(30),
                "1h" => TimeSpan.FromHours(1),
                "6h" => TimeSpan.FromHours(6),
                "24h" => TimeSpan.FromHours(24),
                _ => TimeSpan.FromMinutes(5)
            };
        }

        private static string NormalizeRange(string? range)
        {
            var value = (range ?? "5m").Trim().ToLowerInvariant();
            return value is "5m" or "30m" or "1h" or "6h" or "24h" ? value : "5m";
        }
    }
}
