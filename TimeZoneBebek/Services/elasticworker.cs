using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
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

        // Client khusus n8n agar tidak bocor kredensial Elastic
        private readonly HttpClient _n8nClient;

        // MEMORY TRACKER UNTUK MENCEGAH SPAM (SignalR dan n8n)
        private readonly Dictionary<string, int> _threatTracker = new();

        private const string ElasticUrl = "https://10.2.132.61:9200/.alerts-security.alerts-default,filebeat-*/_search";

        public ElasticWorker(IHubContext<ThreatHub> hubContext, ILogger<ElasticWorker> logger)
        {
            _hubContext = hubContext;
            _logger = logger;

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (m, c, ch, e) => true
            };

            _httpClient = new HttpClient(handler);
            var authString = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("Ariza:Suropati02"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authString);

            // Inisialisasi HTTP Client untuk n8n
            _n8nClient = new HttpClient();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 1. QUERY ELASTICSEARCH (Diperbarui dengan field ECS lengkap)
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
                    var response = await _httpClient.PostAsync(ElasticUrl, content, stoppingToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var jsonString = await response.Content.ReadAsStringAsync();

                        // 2. DESERIALIZE JSON MENGGUNAKAN CETAKAN MODEL BARU
                        var rawData = JsonSerializer.Deserialize<ElasticRawResponse>(jsonString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        var cleanThreats = new List<ThreatModel>();

                        if (rawData?.Aggregations?.TopAttackers?.Buckets != null)
                        {
                            foreach (var bucket in rawData.Aggregations.TopAttackers.Buckets)
                            {
                                var firstHit = bucket.GeoDetails?.Hits?.HitList?.FirstOrDefault()?.Source;

                                if (firstHit != null)
                                {
                                    string ipStr = bucket.Ip ?? "Unknown";
                                    int currentCount = (int)bucket.DocCount;

                                    // LOGIKA DELTA TRACKING (Mencegah Duplicate)
                                    _threatTracker.TryGetValue(ipStr, out int prevCount);
                                    bool isNew = currentCount > prevCount;
                                    _threatTracker[ipStr] = currentCount;

                                    // 3. MENGAMBIL DATA BERSARANG SESUAI MODEL ElasticRawResponse
                                    string orgName = firstHit.SourceExt?.As?.Organization?.Name ?? "Unknown";
                                    string targetHost = firstHit.Host?.Hostname ?? "Unknown";
                                    string queryUrl = firstHit.Url?.Query ?? firstHit.Url?.Original ?? "-";
                                    string httpStatus = firstHit.Http?.Response?.StatusCode.ToString() ?? "0";

                                    // 4. MEMASUKKAN DATA BERSIH KE THREAT MODEL UTAMA
                                    var threatData = new ThreatModel
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

                                        // Tambahan properti kaya konteks
                                        Organization = orgName,
                                        TargetWeb = targetHost,
                                        AttackerQuery = queryUrl,
                                        StatusCode = httpStatus
                                    };

                                    cleanThreats.Add(threatData);
                                }
                            }
                        }

                        // 5. BROADCAST DAN TRIGGER WEBHOOK
                        if (cleanThreats.Count > 0)
                        {
                            // SignalR untuk Dashboard & Geo.html
                            await _hubContext.Clients.All.SendAsync("ReceiveThreats", cleanThreats, stoppingToken);

                            // Trigger n8n hanya untuk serangan/IP yang ada peningkatan hit
                            var newAttacks = cleanThreats.Where(t => t.IsNewEvent).ToList();
                            foreach (var attack in newAttacks)
                            {
                                _ = TriggerN8nWebhookAsync(attack);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Worker Error: {ex.Message}");
                }

                await Task.Delay(5000, stoppingToken);
            }
        }

        private async Task TriggerN8nWebhookAsync(ThreatModel attack)
        {
            try
            {
                // 6. PAYLOAD N8N (Sudah diformat rapi untuk AI)
                var payload = new
                {
                    attacker_ip = attack.Ip,
                    location = attack.Country,
                    organization = attack.Organization,

                    threat_rule = attack.Type,
                    severity = attack.Severity,
                    total_hits = attack.Count,

                    target_web = attack.TargetWeb,
                    attacker_query = attack.AttackerQuery,
                    status_code = attack.StatusCode,

                    timestamp = DateTime.UtcNow.ToString("O")
                };

                // URL n8n yang Anda gunakan saat ini
                string n8nWebhookUrl = "https://n8n.bebekpintar.my.id/webhook-test/b3838a88-9438-425e-b6f9-c845c8a8d2ca";

                await _n8nClient.PostAsJsonAsync(n8nWebhookUrl, payload);
                _logger.LogInformation($"[N8N] Berhasil menembak webhook intelijen untuk IP {attack.Ip}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[N8N] Gagal mengirim webhook n8n untuk IP {attack.Ip}: {ex.Message}");
            }
        }
    }
}