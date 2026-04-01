using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TimeZoneBebek.Models;

namespace TimeZoneBebek.Services
{
    public class ElasticEpsService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<ElasticEpsService> _logger;
        private readonly MonitoringState _monitoringState;
        private readonly string _elasticUrl;

        public ElasticEpsService(ILogger<ElasticEpsService> logger, IConfiguration configuration, MonitoringState monitoringState)
        {
            _logger = logger;
            _monitoringState = monitoringState;

            var elasticConfig = configuration.GetSection("ElasticConfig").Get<ElasticConfig>() ?? new ElasticConfig();
            _elasticUrl = $"{elasticConfig.Url.TrimEnd('/')}/{elasticConfig.IndexPattern}/_count";

            var handler = new HttpClientHandler();
            if (elasticConfig.AllowInvalidCertificate)
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

            _httpClient = new HttpClient(handler);

            if (!string.IsNullOrWhiteSpace(elasticConfig.Username) && !string.IsNullOrWhiteSpace(elasticConfig.Password))
            {
                var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{elasticConfig.Username}:{elasticConfig.Password}"));
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authString);
            }
        }

        public async Task<EpsSnapshot> GetCurrentEpsAsync()
        {
            try
            {
                var epsQuery = """
                {
                    "query": {
                        "range": {
                            "@timestamp": {
                                "gte": "now-1s"
                            }
                        }
                    }
                }
                """;

                var perMinuteQuery = """
                {
                    "query": {
                        "range": {
                            "@timestamp": {
                                "gte": "now-1m"
                            }
                        }
                    }
                }
                """;

                var eventsPerSecond = await CountAsync(epsQuery);
                var eventsLastMinute = await CountAsync(perMinuteQuery);

                var snapshot = new EpsSnapshot
                {
                    EventsPerSecond = eventsPerSecond,
                    EventsLastMinute = eventsLastMinute,
                    CapturedAtUtc = DateTime.UtcNow
                };

                _monitoringState.MarkEpsSuccess(snapshot);
                return snapshot;
            }
            catch (Exception ex)
            {
                _monitoringState.MarkEpsFailure();
                _logger.LogError("Gagal mengambil data event rate: {Message}", ex.Message);
                return new EpsSnapshot { CapturedAtUtc = DateTime.UtcNow };
            }
        }

        private async Task<int> CountAsync(string queryJson)
        {
            var content = new StringContent(queryJson, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(_elasticUrl, content);
            response.EnsureSuccessStatusCode();

            var jsonString = await response.Content.ReadAsStringAsync();
            var jsonDoc = JsonDocument.Parse(jsonString);
            return jsonDoc.RootElement.GetProperty("count").GetInt32();
        }
    }
}
