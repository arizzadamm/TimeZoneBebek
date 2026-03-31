using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Xml.Linq;
using TimeZoneBebek.Helpers;
using TimeZoneBebek.Models;

namespace TimeZoneBebek.Controllers
{
    [ApiController]
    [Route("api")]
    public class SystemApiController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _clientFactory;
        private static List<NewsItem> _cachedNews = new();
        private static DateTime _lastNewsFetch = DateTime.MinValue;

        public SystemApiController(IConfiguration config, IHttpClientFactory clientFactory)
        {
            _config = config;
            _clientFactory = clientFactory;
        }

        [HttpGet("services")]
        public async Task<IActionResult> GetServices()
        {
            var monitoredServices = _config.GetSection("MonitoredServices").Get<List<ServiceConfig>>() ?? new();
            var tasks = monitoredServices.Select(async s => {
                var sw = Stopwatch.StartNew(); bool isOnline = false;
                try { using var c = new TcpClient(); var t1 = c.ConnectAsync(s.Host, s.Port); var t2 = Task.Delay(2000); if (await Task.WhenAny(t1, t2) == t1 && c.Connected) isOnline = true; } catch { }
                sw.Stop();
                return new { name = s.Name, type = s.Type, isOnline, latency = sw.ElapsedMilliseconds };
            });
            return Ok(await Task.WhenAll(tasks));
        }

        [HttpGet("portal-apps")]
        public IActionResult GetPortalApps() => Ok(_config.GetSection("PortalApps").Get<List<PortalAppConfig>>() ?? new());

        [HttpGet("imam-list")]
        public IActionResult GetImamList() => Ok(_config.GetSection("ImamList").Get<string[]>() ?? Array.Empty<string>());

        [HttpGet("geo-trace")]
        [EnableRateLimiting("fixed-by-ip")]
        public async Task<IActionResult> TraceGeo([FromQuery] string? ip)
        {
            if (string.IsNullOrEmpty(ip))
            {
                ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
                if (Request.Headers.ContainsKey("CF-Connecting-IP")) ip = Request.Headers["CF-Connecting-IP"];
                else if (Request.Headers.ContainsKey("X-Forwarded-For")) ip = Request.Headers["X-Forwarded-For"].ToString().Split(',')[0];
            }
            if (ip == "::1" || ip == "127.0.0.1") ip = "";
            if (!string.IsNullOrEmpty(ip) && !System.Net.IPAddress.TryParse(ip, out _)) return BadRequest(new { status = "error", message = "Invalid IP" });

            try
            {
                var client = _clientFactory.CreateClient();
                var url = string.IsNullOrEmpty(ip) ? "http://ip-api.com/json/" : $"http://ip-api.com/json/{ip}";
                return Content(await client.GetStringAsync(url), "application/json");
            }
            catch { return Problem(); }
        }

        [HttpGet("news")]
        public async Task<IActionResult> GetNews()
        {
            if (_cachedNews.Any() && DateTime.Now < _lastNewsFetch.AddMinutes(15)) return Ok(_cachedNews);
            try
            {
                var c = _clientFactory.CreateClient(); var x = XDocument.Parse(await c.GetStringAsync("https://feeds.feedburner.com/TheHackersNews"));
                _cachedNews = x.Descendants("item").Take(8).Select(item => new NewsItem { Title = item.Element("title")?.Value ?? "", Link = item.Element("link")?.Value ?? "#" }).ToList();
                _lastNewsFetch = DateTime.Now;
                return Ok(_cachedNews);
            }
            catch { return Ok(new List<NewsItem>()); }
        }

        // --- ACUNETIX LOGIC ---
        private readonly Dictionary<string, Dictionary<string, string>> n8nConfig = new() {
            { "dev", new() { { "get_scans", "https://n8n.bebekpintar.my.id/webhook/get-accunetix-scans-dev" }, { "generate",  "https://n8n.bebekpintar.my.id/webhook/acunetix-generate-report-dev" } }},
            { "prod", new() { { "get_scans", "https://n8n.bebekpintar.my.id/webhook/0054f746-59e6-4c34-966f-6d4c37395025" }, { "generate",  "https://n8n.bebekpintar.my.id/webhook/448fc2fd-8295-40b5-82c6-1fada2db64aa" } }}
        };

        [HttpGet("acunetix/scans")]
        public async Task<IActionResult> GetAcunetixScans([FromQuery] string? env)
        {
            string e = string.IsNullOrEmpty(env) || !n8nConfig.ContainsKey(env.ToLower()) ? "prod" : env.ToLower();
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var response = await client.GetAsync(n8nConfig[e]["get_scans"]);
                if (response.IsSuccessStatusCode) return Content(await response.Content.ReadAsStringAsync(), "application/json");
                return Problem($"Failed fetch from {e}");
            }
            catch (Exception ex) { return Problem(ex.Message); }
        }

        [HttpPost("acunetix/generate")]
        public async Task<IActionResult> GenerateAcunetix([FromBody] JsonElement body)
        {
            string env = body.TryGetProperty("env", out var e) ? e.GetString()?.ToLower() ?? "prod" : "prod";
            if (!n8nConfig.ContainsKey(env)) env = "prod";

            string scanId = body.GetProperty("scanId").GetString() ?? "";
            string targetUrl = body.GetProperty("targetUrl").GetString() ?? "";
            string templateId = body.GetProperty("templateId").GetString() ?? "";

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                var response = await client.PostAsJsonAsync(n8nConfig[env]["generate"], new { body = new { scan_id = scanId }, template_id = templateId });

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                    string docLink = result.TryGetProperty("docUrl", out var d) ? d.GetString() ?? "#" : "#";

                    var history = await JsonHelper.LoadJson<List<AcunetixHistoryItem>>("acunetix_history.json");
                    history.Insert(0, new AcunetixHistoryItem { Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), TargetUrl = targetUrl, TemplateName = "AI Report", Status = "ready", DownloadLink = docLink, Environment = env.ToUpper() });
                    if (history.Count > 50) history = history.Take(50).ToList();

                    await JsonHelper.SaveJson("acunetix_history.json", history);
                    return Ok(new { message = "Generated", link = docLink });
                }
                return Problem("n8n Error");
            }
            catch (Exception ex) { return Problem(ex.Message); }
        }

        [HttpGet("acunetix/history")]
        public async Task<IActionResult> GetAcunetixHistory() => Ok(await JsonHelper.LoadJson<List<AcunetixHistoryItem>>("acunetix_history.json"));
    }
}