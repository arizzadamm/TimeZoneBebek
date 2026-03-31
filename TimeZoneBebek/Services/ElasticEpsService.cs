using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TimeZoneBebek.Services
{
    public class ElasticEpsService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<ElasticEpsService> _logger;
        private const string ElasticUrl = "https://10.2.132.61:9200/.alerts-security.alerts-default,filebeat-*/_count";

        public ElasticEpsService(ILogger<ElasticEpsService> logger)
        {
            _logger = logger;

            // Bypass SSL untuk IP Lokal
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (m, c, ch, e) => true
            };
            _httpClient = new HttpClient(handler);

            // Konfigurasi Kredensial
            var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes("Ariza:Suropati02"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authString);
        }

        public async Task<int> GetCurrentEpsAsync()
        {
            try
            {
                // Query API _count untuk menghitung log 1 detik terakhir
                var queryJson = """
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
                var content = new StringContent(queryJson, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(ElasticUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    var jsonString = await response.Content.ReadAsStringAsync();
                    var jsonDoc = JsonDocument.Parse(jsonString);

                    // Ekstrak nilai count
                    return jsonDoc.RootElement.GetProperty("count").GetInt32();
                }

                return 0; // Fallback jika response bukan 200 OK
            }
            catch (Exception ex)
            {
                _logger.LogError($"Gagal mengambil data EPS: {ex.Message}");
                return 0; // Fallback jika Elasticsearch mati atau timeout
            }
        }
    }
}