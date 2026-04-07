using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TimeZoneBebek.Models;

namespace TimeZoneBebek.Services
{
    public class NmsMonitorService
    {
        private static readonly string[] CategoryOrder =
        [
            "Status Aplikasi",
            "Status Infrastruktur",
            "Network & Security",
            "Sensor Lingkungan"
        ];

        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;

        public NmsMonitorService(IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
        }

        public int GetRefreshIntervalSeconds()
        {
            var config = _configuration.GetSection("NmsMonitoring").Get<NmsMonitoringConfig>() ?? new NmsMonitoringConfig();
            return Math.Max(config.RefreshIntervalSeconds, 5);
        }

        public async Task<NmsStatusSnapshot> CollectAsync(CancellationToken cancellationToken = default)
        {
            var config = _configuration.GetSection("NmsMonitoring").Get<NmsMonitoringConfig>() ?? new NmsMonitoringConfig();
            var items = config.Items ?? [];
            var itemStatuses = await Task.WhenAll(items.Select(item => CheckItemAsync(item, cancellationToken)));

            var categories = CategoryOrder
                .Select(category => new NmsCategoryStatus
                {
                    Name = category,
                    Items = itemStatuses.Where(item => string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase)).ToList()
                })
                .Where(category => category.Items.Count > 0)
                .ToList();

            foreach (var extraCategory in itemStatuses
                .Select(item => item.Category)
                .Where(category => !string.IsNullOrWhiteSpace(category) && !CategoryOrder.Contains(category, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                categories.Add(new NmsCategoryStatus
                {
                    Name = extraCategory,
                    Items = itemStatuses.Where(item => string.Equals(item.Category, extraCategory, StringComparison.OrdinalIgnoreCase)).ToList()
                });
            }

            var summary = new NmsSummary
            {
                TotalItems = itemStatuses.Length,
                UpCount = itemStatuses.Count(item => item.Status == NmsStatuses.Up),
                DownCount = itemStatuses.Count(item => item.Status == NmsStatuses.Down),
                DegradedCount = itemStatuses.Count(item => item.Status == NmsStatuses.Degraded),
                UnknownCount = itemStatuses.Count(item => item.Status == NmsStatuses.Unknown)
            };

            return new NmsStatusSnapshot
            {
                GeneratedAtUtc = DateTime.UtcNow,
                RefreshIntervalSeconds = Math.Max(config.RefreshIntervalSeconds, 5),
                Summary = summary,
                Categories = categories
            };
        }

        private async Task<NmsItemStatus> CheckItemAsync(NmsItemConfig item, CancellationToken cancellationToken)
        {
            var checkedAtUtc = DateTime.UtcNow;
            var itemStatus = new NmsItemStatus
            {
                Name = item.Name,
                Category = item.Category,
                Description = item.Description,
                CheckType = item.CheckType,
                CheckedAtUtc = checkedAtUtc
            };

            if (string.Equals(item.CheckType, "sensor-static", StringComparison.OrdinalIgnoreCase))
            {
                itemStatus.Status = NmsStatuses.Up;
                itemStatus.Value = item.Value;
                itemStatus.Unit = item.Unit;
                itemStatus.Detail = string.IsNullOrWhiteSpace(item.Description) ? "Baseline sensor value" : item.Description;
                return itemStatus;
            }

            var targets = item.Targets ?? [];
            if (targets.Count == 0)
            {
                itemStatus.Status = NmsStatuses.Unknown;
                itemStatus.Detail = "No monitoring target configured";
                return itemStatus;
            }

            var targetStatuses = await Task.WhenAll(targets.Select(target => CheckTargetAsync(item, target, cancellationToken)));
            itemStatus.Targets = targetStatuses.ToList();

            var upCount = targetStatuses.Count(target => target.Status == NmsStatuses.Up);
            var downCount = targetStatuses.Count(target => target.Status == NmsStatuses.Down);
            var unknownCount = targetStatuses.Count(target => target.Status == NmsStatuses.Unknown);

            itemStatus.Status = upCount == targetStatuses.Length
                ? NmsStatuses.Up
                : upCount == 0 && downCount > 0
                    ? NmsStatuses.Down
                    : upCount > 0
                        ? NmsStatuses.Degraded
                        : NmsStatuses.Unknown;

            var averageLatency = targetStatuses
                .Where(target => target.LatencyMs.HasValue)
                .Select(target => target.LatencyMs!.Value)
                .DefaultIfEmpty()
                .Average();

            itemStatus.LatencyMs = averageLatency > 0 ? Convert.ToInt64(Math.Round(averageLatency)) : null;
            itemStatus.Detail = upCount == targetStatuses.Length
                ? $"{upCount}/{targetStatuses.Length} target healthy"
                : $"{upCount}/{targetStatuses.Length} healthy, {downCount} down, {unknownCount} unknown";

            return itemStatus;
        }

        private async Task<NmsTargetStatus> CheckTargetAsync(NmsItemConfig item, NmsTargetConfig target, CancellationToken cancellationToken)
        {
            var checkType = string.IsNullOrWhiteSpace(target.CheckType) ? item.CheckType : target.CheckType;
            var timeoutMs = target.TimeoutMs > 0 ? target.TimeoutMs : (item.TimeoutMs > 0 ? item.TimeoutMs : 2500);
            var targetStatus = new NmsTargetStatus
            {
                Name = string.IsNullOrWhiteSpace(target.Name) ? item.Name : target.Name,
                CheckType = checkType,
                Target = ResolveTargetAddress(target)
            };

            try
            {
                switch ((checkType ?? "").Trim().ToLowerInvariant())
                {
                    case "ping":
                        await CheckPingAsync(targetStatus, target.Host, timeoutMs);
                        break;
                    case "tcp":
                        await CheckTcpAsync(targetStatus, target.Host, target.Port ?? 0, timeoutMs, cancellationToken);
                        break;
                    case "http":
                        await CheckHttpAsync(targetStatus, target.Url, timeoutMs, cancellationToken);
                        break;
                    default:
                        targetStatus.Status = NmsStatuses.Unknown;
                        targetStatus.Detail = $"Unsupported check type: {checkType}";
                        break;
                }
            }
            catch (Exception ex)
            {
                targetStatus.Status = NmsStatuses.Down;
                targetStatus.Detail = ex.Message;
            }

            return targetStatus;
        }

        private static async Task CheckPingAsync(NmsTargetStatus status, string host, int timeoutMs)
        {
            using var ping = new Ping();
            var stopwatch = Stopwatch.StartNew();
            var reply = await ping.SendPingAsync(host, timeoutMs);
            stopwatch.Stop();

            status.LatencyMs = stopwatch.ElapsedMilliseconds;
            status.Status = reply.Status == IPStatus.Success ? NmsStatuses.Up : NmsStatuses.Down;
            status.Detail = reply.Status == IPStatus.Success
                ? $"ICMP reply from {host}"
                : $"Ping {reply.Status}";
        }

        private static async Task CheckTcpAsync(NmsTargetStatus status, string host, int port, int timeoutMs, CancellationToken cancellationToken)
        {
            using var client = new TcpClient();
            var stopwatch = Stopwatch.StartNew();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, timeoutCts.Token);
            stopwatch.Stop();

            status.LatencyMs = stopwatch.ElapsedMilliseconds;
            status.Status = client.Connected ? NmsStatuses.Up : NmsStatuses.Down;
            status.Detail = client.Connected ? $"TCP {host}:{port} reachable" : $"TCP {host}:{port} unreachable";
        }

        private async Task CheckHttpAsync(NmsTargetStatus status, string url, int timeoutMs, CancellationToken cancellationToken)
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMilliseconds(timeoutMs);

            var stopwatch = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            stopwatch.Stop();

            status.LatencyMs = stopwatch.ElapsedMilliseconds;
            status.Status = response.IsSuccessStatusCode ? NmsStatuses.Up : NmsStatuses.Down;
            status.Detail = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        }

        private static string ResolveTargetAddress(NmsTargetConfig target)
        {
            if (!string.IsNullOrWhiteSpace(target.Url))
                return target.Url;

            if (!string.IsNullOrWhiteSpace(target.Host) && target.Port.HasValue)
                return $"{target.Host}:{target.Port.Value}";

            return target.Host;
        }
    }
}
