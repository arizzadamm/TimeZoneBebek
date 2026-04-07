using Microsoft.AspNetCore.SignalR;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TimeZoneBebek.Hubs;
using TimeZoneBebek.Models;

namespace TimeZoneBebek.Services
{
    public class ElasticWorker : BackgroundService
    {
        private readonly IHubContext<ThreatHub> _hubContext;
        private readonly ILogger<ElasticWorker> _logger;
        private readonly HttpClient _httpClient;
        private readonly MonitoringState _monitoringState;
        private readonly string _elasticUrl;
        private readonly Dictionary<string, int> _threatTracker = new();

        public ElasticWorker(
            IHubContext<ThreatHub> hubContext,
            ILogger<ElasticWorker> logger,
            IConfiguration configuration,
            MonitoringState monitoringState)
        {
            _hubContext = hubContext;
            _logger = logger;
            _monitoringState = monitoringState;

            var elasticConfig = configuration.GetSection("ElasticConfig").Get<ElasticConfig>() ?? new ElasticConfig();
            _elasticUrl = $"{elasticConfig.Url.TrimEnd('/')}/{elasticConfig.IndexPattern}/_search";

            var handler = new HttpClientHandler();
            if (elasticConfig.AllowInvalidCertificate)
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

            _httpClient = new HttpClient(handler);
            if (!string.IsNullOrWhiteSpace(elasticConfig.Username) && !string.IsNullOrWhiteSpace(elasticConfig.Password))
            {
                var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{elasticConfig.Username}:{elasticConfig.Password}"));
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authString);
            }
            
            // Incident creation sekarang hanya boleh datang dari HTTP POST eksternal, mis. n8n.
            _monitoringState.MarkWebhookDisabled();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var queryJson = """
                    {
                      "size": 0,
                      "query": {
                        "bool": {
                          "must": [ { "range": { "@timestamp": { "gte": "now-7d", "lte": "now", "time_zone":"+07:00" } } } ],
                          "must_not": [
                            { "range": { "source.ip": { "gte": "10.2.0.0", "lte": "10.2.255.255" } } },
                            { "match_phrase": { "source.as.organization.name": "CLOUDFLARENET" } }
                          ]
                        }
                      },
                      "aggs": {
                        "top_attackers": {
                          "terms": { "field": "source.ip", "size": 10 },
                          "aggs": {
                            "geo_details": {
                              "top_hits": {
                                "size": 1,
                                "_source": {
                                  "includes": [
                                    "source.geo",
                                    "kibana.alert.rule.name",
                                    "kibana.alert.rule.severity",
                                    "url.query",
                                    "url.original",
                                    "http.response.status_code",
                                    "source.as.organization.name",
                                    "host.hostname"
                                  ]
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                    """;

                    var content = new StringContent(queryJson, Encoding.UTF8, "application/json");
                    var response = await _httpClient.PostAsync(_elasticUrl, content, stoppingToken);
                    response.EnsureSuccessStatusCode();
                    _monitoringState.MarkElasticSuccess();

                    var jsonString = await response.Content.ReadAsStringAsync(stoppingToken);
                    var rawData = JsonSerializer.Deserialize<ElasticRawResponse>(jsonString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    var cleanThreats = new List<ThreatModel>();

                    if (rawData?.Aggregations?.TopAttackers?.Buckets != null)
                    {
                        foreach (var bucket in rawData.Aggregations.TopAttackers.Buckets)
                        {
                            var firstHit = bucket.GeoDetails?.Hits?.HitList?.FirstOrDefault()?.Source;
                            if (firstHit == null)
                                continue;

                            string ipStr = bucket.Ip ?? "Unknown";
                            int currentCount = (int)bucket.DocCount;
                            _threatTracker.TryGetValue(ipStr, out int prevCount);
                            bool isNew = currentCount > prevCount;
                            _threatTracker[ipStr] = currentCount;

                            cleanThreats.Add(new ThreatModel
                            {
                                Ip = ipStr,
                                Count = currentCount,
                                CountryCode = firstHit.SourceExt?.Geo?.CountryIsoCode ?? "vi",
                                Country = firstHit.SourceExt?.Geo?.CountryName ?? "Unknown",
                                Lat = firstHit.SourceExt?.Geo?.Location?.Lat ?? 0,
                                Lon = firstHit.SourceExt?.Geo?.Location?.Lon ?? 0,
                                Type = firstHit.RuleName ?? "SUSPICIOUS TRAFFIC",
                                Severity = firstHit.Severity ?? "medium",
                                IsNewEvent = isNew,
                                Organization = firstHit.SourceExt?.As?.Organization?.Name ?? "Unknown",
                                TargetWeb = firstHit.Host?.Hostname ?? "Unknown",
                                AttackerQuery = firstHit.Url?.Query ?? firstHit.Url?.Original ?? "-",
                                StatusCode = firstHit.Http?.Response?.StatusCode.ToString() ?? "0"
                            });
                        }
                    }

                    if (cleanThreats.Count > 0)
                    {
                        await _hubContext.Clients.All.SendAsync("ReceiveThreats", cleanThreats, stoppingToken);
                        _monitoringState.MarkBroadcast();
                    }
                }
                catch (Exception ex)
                {
                    _monitoringState.MarkElasticFailure(ex.Message);
                    _logger.LogError("Worker Error: {Message}", ex.Message);
                }

                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}
