using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using TimeZoneBebek.Models;

namespace TimeZoneBebek.Controllers
{
    [ApiController]
    [Route("api/threats")]
    public class ThreatController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _clientFactory;

        public ThreatController(IConfiguration config, IHttpClientFactory clientFactory)
        {
            _config = config;
            _clientFactory = clientFactory;
        }

        [HttpGet]
        public async Task<IActionResult> GetElasticThreats()
        {
            try
            {
                var esConfig = _config.GetSection("ElasticConfig").Get<ElasticConfig>();
                if (esConfig == null || string.IsNullOrEmpty(esConfig.Url)) return Problem("ElasticConfig Missing");

                var queryJson = "{\"size\": 0, \"query\": { \"bool\": { \"must\": [ { \"range\": { \"@timestamp\": { \"gte\": \"now-7d\", \"lte\": \"now\", \"time_zone\":\"+07:00\" } } } ], \"must_not\": [ { \"range\": { \"source.ip\": { \"gte\": \"10.2.0.0\", \"lte\": \"10.2.255.255\" } } }, { \"match_phrase\": { \"source.as.organization.name\": \"CLOUDFLARENET\" } } ] } }, \"aggs\": { \"top_attackers\": { \"terms\": { \"field\": \"source.ip\", \"size\": 10 } } } }";

                var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (m, c, ch, e) => true };
                using var client = new HttpClient(handler);
                var authBytes = Encoding.ASCII.GetBytes($"{esConfig.Username}:{esConfig.Password}");
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

                var response = await client.PostAsync($"{esConfig.Url.TrimEnd('/')}/{esConfig.IndexPattern}/_search", new StringContent(queryJson, Encoding.UTF8, "application/json"));
                var resBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) return Problem($"Elastic Error: {resBody}");

                var jsonDoc = JsonDocument.Parse(resBody);
                if (!jsonDoc.RootElement.TryGetProperty("aggregations", out var aggs) || !aggs.TryGetProperty("top_attackers", out var top) || !top.TryGetProperty("buckets", out var bucketsProp))
                    return Problem("Invalid Elastic Response Structure");

                var threats = new List<ThreatData>();
                using var geoClient = _clientFactory.CreateClient();

                foreach (var bucket in bucketsProp.EnumerateArray())
                {
                    string ip = bucket.GetProperty("key").GetString() ?? "";
                    int count = bucket.GetProperty("doc_count").GetInt32();
                    if (ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip == "127.0.0.1") continue;

                    try
                    {
                        var geoRes = await geoClient.GetStringAsync($"http://ip-api.com/json/{ip}");
                        var geoJson = JsonDocument.Parse(geoRes).RootElement;
                        if (geoJson.GetProperty("status").GetString() == "success")
                        {
                            threats.Add(new ThreatData { Ip = ip, Count = count, Lat = geoJson.GetProperty("lat").GetDouble(), Lon = geoJson.GetProperty("lon").GetDouble(), Country = geoJson.GetProperty("countryCode").GetString() ?? "" });
                        }
                    }
                    catch { }
                }
                return Ok(threats);
            }
            catch (Exception ex) { return Problem(ex.Message); }
        }
    }
}