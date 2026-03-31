using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
// SECURITY: RATE LIMITING
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Aturan: "FixedWindow" -> Maksimal 10 request per 1 menit per IP
    options.AddPolicy("fixed-by-ip", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
});
// Load Config
var monitoredServices = builder.Configuration.GetSection("MonitoredServices").Get<List<ServiceConfig>>() ?? new List<ServiceConfig>();
var portalApps = builder.Configuration.GetSection("PortalApps").Get<List<PortalAppConfig>>() ?? new List<PortalAppConfig>();
var app = builder.Build();

app.UseStaticFiles(); // Enable wwwroot
app.UseRateLimiter(); // Enable Rate Limiting Middleware

// SECURITY HEADERS
app.Use(async (context, next) => {
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("Content-Security-Policy",
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' https://unpkg.com https://cdn.jsdelivr.net; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://unpkg.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data: https://tile.openstreetmap.org https://unpkg.com; " +
        "connect-src 'self' http://ip-api.com https://feeds.feedburner.com;");

    await next();
});

// --- HELPERS ---
string GetSidebar(string active) => $@"
    <script src='https://cdn.jsdelivr.net/npm/particles.js@2.0.0/particles.min.js'></script>
    <script src='https://unpkg.com/typed.js@2.0.16/dist/typed.umd.js'></script>
    <link rel='stylesheet' type='text/css' href='https://cdn.jsdelivr.net/npm/toastify-js/src/toastify.min.css'>
    <script type='text/javascript' src='https://cdn.jsdelivr.net/npm/toastify-js'></script>

    <div id='mySidebar' class='sidebar'>
        <div style='text-align:center; margin-bottom:20px; color:#fff; font-weight:bold;'>SYSTEM MENU</div>
        <a href='/' class='{(active == "home" ? "active" : "")}'>DASHBOARD</a>
        <a href='/portal' class='{(active == "portal" ? "active" : "")}'>APP PORTAL</a>
        <a href='/geo' class='{(active == "geo" ? "active" : "")}'>GEO TRACER</a>
        <a href='/monitor' class='{(active == "monitor" ? "active" : "")}'>SERVICE MONITOR</a>
        
    </div>
    <div id='myOverlay' class='overlay' onclick='toggleNav()'></div>
    <span class='menu-btn' onclick='toggleNav()'>&#9776;</span>
    
    <script>
        function toggleNav(){{document.getElementById('mySidebar').classList.toggle('open');document.getElementById('myOverlay').classList.toggle('active');}}
        
        // --- GLOBAL: INIT PARTICLES (Background Keren) ---
        // Kita pasang div particles di body jika belum ada
        if(!document.getElementById('particles-js')) {{
            const p = document.createElement('div');
            p.id = 'particles-js';
            p.style.position = 'fixed'; p.style.top = '0'; p.style.left = '0';
            p.style.width = '100%'; p.style.height = '100%'; p.style.zIndex = '-2'; // Di belakang grid
            document.body.appendChild(p);
            
            // Config Particles (Cyan Network)
            particlesJS('particles-js', {{
              'particles': {{
                'number': {{ 'value': 40, 'density': {{ 'enable': true, 'value_area': 800 }} }},
                'color': {{ 'value': '#00ffcc' }},
                'shape': {{ 'type': 'circle' }},
                'opacity': {{ 'value': 0.3, 'random': false }},
                'size': {{ 'value': 2, 'random': true }},
                'line_linked': {{ 'enable': true, 'distance': 150, 'color': '#00ffcc', 'opacity': 0.2, 'width': 1 }},
                'move': {{ 'enable': true, 'speed': 1, 'direction': 'none', 'random': false, 'straight': false, 'out_mode': 'out', 'bounce': false }}
              }},
              'interactivity': {{
                'detect_on': 'canvas',
                'events': {{ 'onhover': {{ 'enable': true, 'mode': 'grab' }}, 'onclick': {{ 'enable': true, 'mode': 'push' }}, 'resize': true }},
                'modes': {{ 'grab': {{ 'distance': 140, 'line_linked': {{ 'opacity': 0.5 }} }} }}
              }},
              'retina_detect': true
            }});
        }}
    </script>";
string GetPreloader() => @"
    <div id='preloader'><div class='duck-loader'>🦆</div><div class='loading-text'>INITIALIZING SYSTEM...</div></div>
    <script>window.addEventListener('load', ()=>{ setTimeout(()=>{ document.body.classList.add('loaded'); setTimeout(()=>{ document.getElementById('preloader').style.display='none';},800); },500); });</script>";

// --- PAGE ENDPOINTS ---

app.MapGet("/", async context => {
    var process = Process.GetCurrentProcess();
    var uptime = DateTime.Now - process.StartTime;
    var osDesc = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    string diskInfo = "N/A"; string diskClass = ""; int diskPct = 0; // Variable baru
    try
    {
        var d = DriveInfo.GetDrives().FirstOrDefault(x => x.IsReady && (x.Name == "/" || x.Name.StartsWith("C")));
        if (d != null)
        {
            double t = 1024.0 * 1024.0 * 1024.0;
            double f = d.AvailableFreeSpace / t;
            double tot = d.TotalSize / t;
            diskPct = 100 - (int)((f / tot) * 100); // Terpakai %
            diskInfo = $"{f:F1} GB Free"; // Teks lebih pendek
            if (f < 2) diskClass = "alert";
        }
    }
    catch { }


    string ramInfo = "N/A"; int ramPct = 0; // Variable baru
    if (OperatingSystem.IsLinux())
    {
        try
        {
            var lines = await File.ReadAllLinesAsync("/proc/meminfo");
            long GetVal(string k) => long.Parse(lines.First(l => l.StartsWith(k)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
            double t = GetVal("MemTotal:") / 1024.0 / 1024.0;
            double u = t - (GetVal("MemAvailable:") / 1024.0 / 1024.0);
            ramInfo = $"{u:F1} / {t:F1} GB";
            ramPct = (int)((u / t) * 100); // Hitung %
        }
        catch { }
    }
    else
    {
        ramInfo = "App Mode"; ramPct = 10; // Dummy visual buat Windows dev
    }

    var headerDump = new StringBuilder(); foreach (var h in context.Request.Headers) headerDump.AppendLine($"<div class='row'><span class='key'>{h.Key}:</span> <span class='val'>{h.Value}</span></div>");

    var ip = context.Connection.RemoteIpAddress?.ToString();
    if (context.Request.Headers.ContainsKey("CF-Connecting-IP")) ip = context.Request.Headers["CF-Connecting-IP"];
    else if (context.Request.Headers.ContainsKey("X-Forwarded-For")) ip = context.Request.Headers["X-Forwarded-For"];

    string html = await File.ReadAllTextAsync("wwwroot/pages/dashboard.html");
    html = html.Replace("{{PRELOADER}}", GetPreloader()).Replace("{{SIDEBAR}}", GetSidebar("home"))
               .Replace("{{OS_DESC}}", osDesc)

               // REPLACE DATA BARU
               .Replace("{{RAM_DESC}}", ramInfo).Replace("{{RAM_PCT}}", ramPct.ToString())
               .Replace("{{DISK_DESC}}", diskInfo).Replace("{{DISK_PCT}}", diskPct.ToString())

               .Replace("{{DISK_CLASS}}", diskClass)
               .Replace("{{UPTIME}}", $"{uptime.Days}d {uptime.Hours}h")
               .Replace("{{REMOTE_IP}}", ip ?? "Unknown").Replace("{{PROTOCOL}}", context.Request.Protocol)
               .Replace("{{HEADER_DUMP}}", headerDump.ToString());

    context.Response.ContentType = "text/html";
    await context.Response.WriteAsync(html);

});

app.MapGet("/geo", async context => {
    string html = await File.ReadAllTextAsync("wwwroot/pages/geo.html");
    html = html.Replace("{{PRELOADER}}", GetPreloader()).Replace("{{SIDEBAR}}", GetSidebar("geo"));
    context.Response.ContentType = "text/html";
    await context.Response.WriteAsync(html);
});

app.MapGet("/monitor", async context => {
    string html = await File.ReadAllTextAsync("wwwroot/pages/monitor.html");
    html = html.Replace("{{PRELOADER}}", GetPreloader()).Replace("{{SIDEBAR}}", GetSidebar("monitor"));
    context.Response.ContentType = "text/html";
    await context.Response.WriteAsync(html);
});

//app.MapGet("/latency", async context => {
//    string html = await File.ReadAllTextAsync("wwwroot/pages/latency.html");
//    html = html.Replace("{{PRELOADER}}", GetPreloader()).Replace("{{SIDEBAR}}", GetSidebar("latency"));
//    context.Response.ContentType = "text/html";
//    await context.Response.WriteAsync(html);
//});

// --- API ENDPOINTS ---

app.MapGet("/api/services", async () => {
    var tasks = monitoredServices.Select(async s => {
        var sw = Stopwatch.StartNew(); bool isOnline = false; try { using var c = new TcpClient(); var t1 = c.ConnectAsync(s.Host, s.Port); var t2 = Task.Delay(2000); if (await Task.WhenAny(t1, t2) == t1 && c.Connected) isOnline = true; } catch { }
        sw.Stop();
        return new { name = s.Name, type = s.Type, isOnline, latency = sw.ElapsedMilliseconds };
    });
    return Results.Ok(await Task.WhenAll(tasks));
});

//app.MapGet("/api/pings", async () => {
//    long PingHost(string h) { try { using var p = new Ping(); var r = p.Send(h, 1000); return r.Status == IPStatus.Success ? r.RoundtripTime : -1; } catch { return -1; } }
//    var t1 = Task.Run(() => PingHost("1.1.1.1")); var t2 = Task.Run(() => PingHost("8.8.8.8")); await Task.WhenAll(t1, t2);
//    return Results.Ok(new { cf = t1.Result, go = t2.Result });
//});

// PAGE: PORTAL LAUNCHPAD
app.MapGet("/portal", async context => {
    string html = await File.ReadAllTextAsync("wwwroot/pages/portal.html");
    html = html.Replace("{{PRELOADER}}", GetPreloader()).Replace("{{SIDEBAR}}", GetSidebar("portal"));
    context.Response.ContentType = "text/html";
    await context.Response.WriteAsync(html);
});

// API: GET APPS LIST
app.MapGet("/api/portal-apps", () => Results.Ok(portalApps));

app.MapGet("/api/geo-trace", async (HttpContext context, IHttpClientFactory f) => {
    string ipToTrace = "";

    // 1. Ambil Input
    if (context.Request.Query.ContainsKey("ip"))
    {
        ipToTrace = context.Request.Query["ip"];
    }
    else
    {
        // Auto-detect logic...
        ipToTrace = context.Connection.RemoteIpAddress?.ToString() ?? "";
        if (context.Request.Headers.ContainsKey("CF-Connecting-IP")) ipToTrace = context.Request.Headers["CF-Connecting-IP"];
        else if (context.Request.Headers.ContainsKey("X-Forwarded-For")) ipToTrace = context.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0];
    }

    // Bersihkan Localhost
    if (ipToTrace == "::1" || ipToTrace == "127.0.0.1") ipToTrace = "";

    // --- SECURITY: INPUT VALIDATION ---
    // Jika IP tidak kosong DAN formatnya BUKAN IP Address yang valid -> TOLAK!
    // Ini mematikan potensi XSS dan Injection di level akar.
    if (!string.IsNullOrEmpty(ipToTrace) && !System.Net.IPAddress.TryParse(ipToTrace, out _))
    {
        return Results.BadRequest(new { status = "error", message = "Invalid IP Address Format" });
    }
    // ----------------------------------

    try
    {
        var client = f.CreateClient();
        var url = string.IsNullOrEmpty(ipToTrace) ? "http://ip-api.com/json/" : $"http://ip-api.com/json/{ipToTrace}";
        var json = await client.GetStringAsync(url);
        return Results.Content(json, "application/json");
    }
    catch { return Results.Problem(); }

}).RequireRateLimiting("fixed-by-ip"); // <--- Terapkan Rate Limit disini

// Simple News Cache
List<NewsItem> _cachedNews = new(); DateTime _lastNewsFetch = DateTime.MinValue;
app.MapGet("/api/news", async (IHttpClientFactory f) => {
    if (_cachedNews.Any() && DateTime.Now < _lastNewsFetch.AddMinutes(15)) return Results.Ok(_cachedNews);
    try
    {
        var c = f.CreateClient(); var x = XDocument.Parse(await c.GetStringAsync("https://feeds.feedburner.com/TheHackersNews"));
        var i = x.Descendants("item").Take(8).Select(item => new NewsItem { Title = item.Element("title")?.Value ?? "", Link = item.Element("link")?.Value ?? "#" }).ToList();
        _cachedNews = i; _lastNewsFetch = DateTime.Now; return Results.Ok(i);
    }
    catch { return Results.Ok(new List<NewsItem>()); }
});
// --- 6. API THREAT INTELLIGENCE (ELASTICSEARCH INTEGRATION) ---
app.MapGet("/api/threats", async (IConfiguration config, IHttpClientFactory clientFactory) => {
    try
    {
        var esConfig = config.GetSection("ElasticConfig").Get<ElasticConfig>();

        // VALIDASI CONFIG
        if (esConfig == null || string.IsNullOrEmpty(esConfig.Url))
            return Results.Problem("Konfigurasi ElasticConfig kosong di appsettings.json");

        var queryJson = """
        {
          "size": 0,
          "query": {
            "bool": {
              "must": [ { "range": { "@timestamp": { "gte": "now-5m", "lte": "now" } } } ],
              "must_not": [
                { "range": { "source.ip": { "gte": "10.2.0.0", "lte": "10.2.255.255" } } },
                { "match_phrase": { "source.as.organization.name": "CLOUDFLARENET" } }
              ]
            }
          },
          "aggs": { "top_attackers": { "terms": { "field": "source.ip", "size": 10 } } }
        }
        """;

        // 1. SETUP HTTP CLIENT (Bypass SSL)
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (m, c, ch, e) => true };
        using var client = new HttpClient(handler);

        // Auth
        var authBytes = Encoding.ASCII.GetBytes($"{esConfig.Username}:{esConfig.Password}");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        // 2. KIRIM REQUEST
        var content = new StringContent(queryJson, Encoding.UTF8, "application/json");
        // PENTING: Pastikan URL tidak double slash. Hapus slash di akhir config jika ada.
        var targetUrl = $"{esConfig.Url.TrimEnd('/')}/{esConfig.IndexPattern}/_search";

        Console.WriteLine($"[DEBUG] Connecting to: {targetUrl}"); // Log ke Terminal

        var response = await client.PostAsync(targetUrl, content);
        var resBody = await response.Content.ReadAsStringAsync();

        // 3. CEK RESPONSE ELASTIC
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[ERROR ELASTIC] {response.StatusCode}: {resBody}");
            // Kembalikan error asli ke browser agar bisa dibaca
            return Results.Problem($"Elastic Error ({response.StatusCode}): {resBody}");
        }

        // 4. PARSING JSON
        var jsonDoc = System.Text.Json.JsonDocument.Parse(resBody);

        // Cek apakah ada error di dalam JSON body
        if (jsonDoc.RootElement.TryGetProperty("error", out var errorProp))
        {
            var reason = errorProp.GetProperty("reason").GetString();
            Console.WriteLine($"[ELASTIC QUERY FAIL] {reason}");
            return Results.Problem($"Elastic Query Fail: {reason}");
        }

        // Cek path aggregations
        if (!jsonDoc.RootElement.TryGetProperty("aggregations", out var aggs) ||
            !aggs.TryGetProperty("top_attackers", out var top) ||
            !top.TryGetProperty("buckets", out var bucketsProp))
        {
            Console.WriteLine($"[PARSE ERROR] Struktur JSON tidak sesuai. Response: {resBody}");
            return Results.Problem("Struktur JSON Elastic tidak memiliki 'aggregations.top_attackers.buckets'");
        }

        var buckets = bucketsProp.EnumerateArray();
        var threats = new List<ThreatData>();
        using var geoClient = clientFactory.CreateClient();

        foreach (var bucket in buckets)
        {
            string ip = bucket.GetProperty("key").GetString();
            int count = bucket.GetProperty("doc_count").GetInt32();

            // Skip IP Internal
            if (ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip == "127.0.0.1") continue;

            try
            {
                // Geo API call...
                var geoRes = await geoClient.GetStringAsync($"http://ip-api.com/json/{ip}");
                var geoJson = System.Text.Json.JsonDocument.Parse(geoRes).RootElement;
                if (geoJson.GetProperty("status").GetString() == "success")
                {
                    threats.Add(new ThreatData
                    {
                        Ip = ip,
                        Count = count,
                        Lat = geoJson.GetProperty("lat").GetDouble(),
                        Lon = geoJson.GetProperty("lon").GetDouble(),
                        Country = geoJson.GetProperty("countryCode").GetString()
                    });
                }
            }
            catch { }
        }

        return Results.Ok(threats);
    }
    catch (Exception ex)
    {
        // CATCH ALL: Ini yang menangkap error 500 dan menampilkannya
        Console.WriteLine($"[CRITICAL EXCEPTION] {ex.Message} \n {ex.StackTrace}");
        return Results.Problem($"Internal Server Error: {ex.Message}");
    }
});
app.Run();

class ServiceConfig { public string Name { get; set; } = ""; public string Host { get; set; } = ""; public int Port { get; set; } public string Type { get; set; } = ""; }
class NewsItem { public string Title { get; set; } = ""; public string Link { get; set; } = ""; }
class ElasticConfig { public string Url { get; set; } public string Username { get; set; } public string Password { get; set; } public string IndexPattern { get; set; } }
class ThreatData { public string Ip { get; set; } public int Count { get; set; } public double Lat { get; set; } public double Lon { get; set; } public string Country { get; set; } }
class PortalAppConfig
{
    public string Name { get; set; }
    public string Url { get; set; }
    public string Description { get; set; }
    public string Category { get; set; } // SEC, DEV, DATA, TOOL
}