using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using System.Xml.Linq;
using TimeZoneBebek.Hubs;
using TimeZoneBebek.Services;

var _fileLock = new SemaphoreSlim(1, 1);
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()  
              .AllowAnyMethod()
              .AllowAnyHeader(); 
    });
});
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
builder.Services.AddSignalR();
builder.Services.AddHostedService<ElasticWorker>(); // Jalankan worker di background

// Load Config
var monitoredServices = builder.Configuration.GetSection("MonitoredServices").Get<List<ServiceConfig>>() ?? new List<ServiceConfig>();
var portalApps = builder.Configuration.GetSection("PortalApps").Get<List<PortalAppConfig>>() ?? new List<PortalAppConfig>();
var app = builder.Build();
app.UseCors("AllowAll");
app.Use(async (context, next) =>
{
    // 1. IZINKAN CORS PREFLIGHT (OPTIONS) LEWAT
    // Browser mengirim ini tanpa key untuk cek izin, jadi harus di-loloskan.
    if (context.Request.Method.ToUpper() == "OPTIONS")
    {
        await next();
        return;
    }
    // 2. Lewati pengecekan untuk file statis (HTML, CSS, JS)
    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }

    // 3. Cek Header X-API-KEY
    var headerKey = context.Request.Headers.Keys.FirstOrDefault(k => k.Equals("X-API-KEY", StringComparison.OrdinalIgnoreCase));

    if (headerKey == null || !context.Request.Headers.TryGetValue(headerKey, out var extractedApiKey))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { message = "Access Denied: Missing API Key" });
        return;
    }

    // 4. Validasi Key dengan AppSettings
    var configuredApiKey = app.Configuration["Authentication:ApiKey"];

    // Pastikan config key ada
    if (string.IsNullOrEmpty(configuredApiKey))
    {
        context.Response.StatusCode = 500;
        return;
    }

    if (!string.Equals(configuredApiKey, extractedApiKey.ToString().Trim()))
    {
        context.Response.StatusCode = 403; // Forbidden
        await context.Response.WriteAsJsonAsync(new { message = "Access Denied: Invalid API Key" });
        return;
    }
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("Content-Security-Policy",
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' https://unpkg.com https://cdn.jsdelivr.net https://api.aladhan.com https://cdnjs.cloudflare.com/ajax/libs/html2pdf.js/0.10.1/html2pdf.bundle.min.js; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data: https://tile.openstreetmap.org https://unpkg.com https://cdn.jsdelivr.net; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://unpkg.com https://cdn.jsdelivr.net; " +
        "connect-src 'self' http://ip-api.com https://feeds.feedburner.com https://api.aladhan.com https://cdn.jsdelivr.net https://cdnjs.cloudflare.com/ajax/libs/html2pdf.js/0.10.1/html2pdf.bundle.min.js;");


    await next();
});
app.UseStaticFiles(); // Enable wwwroot
app.UseRateLimiter(); // Enable Rate Limiting Middleware


// SECURITY HEADERS

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
        <a href='/archive' class='{(active == "archive" ? "active" : "")}'>INCIDENT ARCHIVE</a>
        
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

app.MapGet("/geoold", async context => {
    string html = await File.ReadAllTextAsync("wwwroot/pages/geoold.html");
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

app.MapGet("/reports", async context => {
    string html = await File.ReadAllTextAsync("wwwroot/pages/reports.html");
    html = html.Replace("{{PRELOADER}}", GetPreloader()).Replace("{{SIDEBAR}}", GetSidebar("reports"));
    context.Response.ContentType = "text/html";
    await context.Response.WriteAsync(html);
});

app.MapGet("/archive", async context => {
    string path = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot/pages/archive.html");

    if (!File.Exists(path))
    {
        context.Response.StatusCode = 404;
        return;
    }

    string html = await File.ReadAllTextAsync(path);

    
    html = html.Replace("{{PRELOADER}}", GetPreloader())
               .Replace("{{SIDEBAR}}", GetSidebar("archive"));

    context.Response.ContentType = "text/html";
    await context.Response.WriteAsync(html);
});


// --- API ENDPOINTS ---

app.MapGet("/api/services", async () => {
    var tasks = monitoredServices.Select(async s => {
        var sw = Stopwatch.StartNew(); bool isOnline = false; try { using var c = new TcpClient(); var t1 = c.ConnectAsync(s.Host, s.Port); var t2 = Task.Delay(2000); if (await Task.WhenAny(t1, t2) == t1 && c.Connected) isOnline = true; } catch { }
        sw.Stop();
        return new { name = s.Name, type = s.Type, isOnline, latency = sw.ElapsedMilliseconds };
    });
    return Results.Ok(await Task.WhenAll(tasks));
});

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

}).RequireRateLimiting("fixed-by-ip"); 

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
              "must": [ { "range": { "@timestamp": { "gte": "now-7d", "lte": "now", "time_zone":"+07:00" } } } ],
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
        var targetUrl = $"{esConfig.Url.TrimEnd('/')}/{esConfig.IndexPattern}/_search";

        Console.WriteLine($"[DEBUG] Connecting to: {targetUrl}"); // Log ke Terminal

        var response = await client.PostAsync(targetUrl, content);
        var resBody = await response.Content.ReadAsStringAsync();

        // 3. CEK RESPONSE ELASTIC
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[ERROR ELASTIC] {response.StatusCode}: {resBody}");
            return Results.Problem($"Elastic Error ({response.StatusCode}): {resBody}");
        }

        var jsonDoc = System.Text.Json.JsonDocument.Parse(resBody);

        if (jsonDoc.RootElement.TryGetProperty("error", out var errorProp))
        {
            var reason = errorProp.GetProperty("reason").GetString();
            Console.WriteLine($"[ELASTIC QUERY FAIL] {reason}");
            return Results.Problem($"Elastic Query Fail: {reason}");
        }
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
        Console.WriteLine($"[CRITICAL EXCEPTION] {ex.Message} \n {ex.StackTrace}");
        return Results.Problem($"Internal Server Error: {ex.Message}");
    }
});

//ACCUNETIX 
app.MapGet("/api/acunetix/scans", async () => {
    try
    {
        // URL Webhook n8n yang mengambil list scan dari Acunetix
        var n8nUrl = "https://n8n.bebekpintar.my.id/webhook/get-accunetix-scans-dev";

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(30); // Timeout standar

        // Call n8n
        var response = await client.GetAsync(n8nUrl);

        if (response.IsSuccessStatusCode)
        {
            // Langsung lempar JSON dari n8n ke Frontend
            var json = await response.Content.ReadAsStringAsync();
            return Results.Content(json, "application/json");
        }
        else
        {
            return Results.Problem("Gagal mengambil data scan dari n8n.");
        }
    }
    catch (Exception ex)
    {
        return Results.Problem("Error: " + ex.Message);
    }
});

app.MapPost("/api/acunetix/generate", async (HttpRequest req) => {
    var body = await req.ReadFromJsonAsync<JsonElement>();

    // Ambil data dari Frontend
    string scanId = body.GetProperty("scanId").GetString();
    string targetUrl = body.GetProperty("targetUrl").GetString();
    string templateId = body.GetProperty("templateId").GetString();

    try
    {
        // 1. Persiapan Call n8n
        var n8nUrl = "https://n8n.bebekpintar.my.id/webhook-test/acunetix-generate-report-dev"; // URL Webhook Anda

        using var client = new HttpClient();
        // PENTING: Perpanjang timeout karena n8n butuh waktu untuk AI & Google Docs
        client.Timeout = TimeSpan.FromMinutes(5);

        // Payload ke n8n (Sesuai parameter webhook Anda)
        var n8nPayload = new
        {
            body = new { scan_id = scanId }, // Sesuaikan struktur input n8n Anda
            template_id = templateId
        };

        // 2. Kirim Request & TUNGGU Hasilnya
        var response = await client.PostAsJsonAsync(n8nUrl, n8nPayload);

        if (response.IsSuccessStatusCode)
        {
            // 3. Tangkap Respons JSON dari n8n
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();

            // Ambil Link Google Docs dari n8n
            string docLink = "#";
            if (result.TryGetProperty("docUrl", out var docUrlProp))
            {
                docLink = docUrlProp.GetString();
            }

            // 4. SIMPAN KE HISTORY (JSON LOKAL)
            var path = Path.Combine(Directory.GetCurrentDirectory(), "data", "acunetix_history.json");
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

            List<AcunetixHistoryItem> historyList = new();
            if (File.Exists(path))
            {
                var existingJson = await File.ReadAllTextAsync(path);
                historyList = System.Text.Json.JsonSerializer.Deserialize<List<AcunetixHistoryItem>>(existingJson, options) ?? new();
            }

            // Tambah Item Baru dengan Status "READY" dan Link Docs
            historyList.Insert(0, new AcunetixHistoryItem
            {
                Id = Guid.NewGuid().ToString(),
                Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                TargetUrl = targetUrl,
                TemplateName = "Laporan VA", // Bisa disesuaikan
                Status = "ready", // Langsung READY karena kita menunggu n8n selesai
                DownloadLink = docLink // Link Google Docs
            });

            // Limit History 50 item
            if (historyList.Count > 50) historyList = historyList.Take(50).ToList();

            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(historyList, options));

            return Results.Ok(new { message = "Report generated", link = docLink });
        }
        else
        {
            return Results.Problem("n8n Error: " + response.StatusCode);
        }
    }
    catch (Exception ex)
    {
        return Results.Problem("Timeout/Error: " + ex.Message);
    }
});

app.MapGet("/api/acunetix/history", async () => {
    var path = Path.Combine(Directory.GetCurrentDirectory(), "data", "acunetix_history.json");
    if (!File.Exists(path)) return Results.Ok(new List<object>()); // Return array kosong

    var json = await File.ReadAllTextAsync(path);
    return Results.Content(json, "application/json");
});



// API: GET LIST IMAM
app.MapGet("/api/imam-list", (IConfiguration config) => {
    var list = config.GetSection("ImamList").Get<string[]>() ?? Array.Empty<string>();
    return Results.Ok(list);
});
// ==========================================
// API ENDPOINTS (CRUD LENGKAP)
// ==========================================

// 1. GET ALL (UPDATED FIX)
app.MapGet("/api/incidents", async () => {
    var path = Path.Combine(Directory.GetCurrentDirectory(), "data", "incidents.json");

    if (!File.Exists(path)) return Results.Ok(new List<object>());

    var json = await File.ReadAllTextAsync(path);

    // (Deserialize) agar bisa mentoleransi huruf besar/kecil
    var options = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    var list = System.Text.Json.JsonSerializer.Deserialize<List<Incident>>(json, options);

    
    return Results.Ok(list);
});

// 2. POST (CREATE) - DENGAN DUPLICATE CHECK
app.MapPost("/api/incidents", async (HttpContext context, Incident newInc) => {
    var path = Path.Combine(Directory.GetCurrentDirectory(), "data", "incidents.json");

    var options = new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    List<Incident> list;

    if (File.Exists(path))
    {
        var oldJson = await File.ReadAllTextAsync(path);
        list = System.Text.Json.JsonSerializer.Deserialize<List<Incident>>(oldJson, options) ?? new List<Incident>();
    }
    else
    {
        list = new List<Incident>();
    }

    if (newInc.Date == default) newInc.Date = DateTime.Now;


    bool isDuplicateId = !string.IsNullOrEmpty(newInc.Id) && list.Any(x => x.Id == newInc.Id);

    bool isDuplicateContent = list.Any(x =>
        string.Equals(x.Title, newInc.Title, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.Attacker, newInc.Attacker, StringComparison.OrdinalIgnoreCase) &&
        x.Date.Date == newInc.Date.Date 
    );

    if (isDuplicateId)
    {
        return Results.Conflict(new { message = $"DUPLICATE ID: {newInc.Id} already exists." });
    }

    if (isDuplicateContent)
    {
        // 409 Conflict adalah kode standar untuk duplikat
        return Results.Conflict(new { message = "DUPLICATE ENTRY: Similar incident already recorded today." });
    }
    // ---------------------------------

    // Jika ID kosong (Log baru dari n8n), generate ID unik
    if (string.IsNullOrEmpty(newInc.Id))
    {
        // Tambahkan Random Guid sedikit agar kalau ada 2 log detik yg sama tidak bentrok
        newInc.Id = "INC-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + "-" + Guid.NewGuid().ToString().Substring(0, 4).ToUpper();
    }

    list.Insert(0, newInc); 


    await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(list, options));

    return Results.Ok(new { message = "Incident Logged Successfully", id = newInc.Id });
});

// 3. PUT (UPDATE) - VERSI FIX
app.MapPut("/api/incidents/{id}", async (string id, Incident updatedInc) => {
    var path = Path.Combine(Directory.GetCurrentDirectory(), "data", "incidents.json");
    if (!File.Exists(path)) return Results.NotFound();

    var json = await File.ReadAllTextAsync(path);

    var options = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    var list = System.Text.Json.JsonSerializer.Deserialize<List<Incident>>(json, options) ?? new List<Incident>();

    var index = list.FindIndex(x => x.Id == id);
    if (index == -1) return Results.NotFound(new { message = "Incident ID Not Found" });

    // Update Logic
    var existing = list[index];
    existing.Title = updatedInc.Title;
    existing.Severity = updatedInc.Severity;
    existing.Attacker = updatedInc.Attacker;
    existing.Summary = updatedInc.Summary;
    existing.Tags = updatedInc.Tags;
    if (!string.IsNullOrEmpty(updatedInc.AiAnalysis)) existing.AiAnalysis = updatedInc.AiAnalysis;

    list[index] = existing;

    await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(list, options));

    return Results.Ok(new { message = "Incident Updated" });
});

// 4. DELETE (HAPUS) - VERSI FIX
app.MapDelete("/api/incidents/{id}", async (string id) => {
    var path = Path.Combine(Directory.GetCurrentDirectory(), "data", "incidents.json");
    if (!File.Exists(path)) return Results.NotFound();

    var json = await File.ReadAllTextAsync(path);

    var options = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    var list = System.Text.Json.JsonSerializer.Deserialize<List<Incident>>(json, options) ?? new List<Incident>();

    var item = list.FirstOrDefault(x => x.Id.Trim() == id.Trim());

    if (item == null) return Results.NotFound(new { message = $"Incident ID '{id}' Not Found in Database." });

    list.Remove(item);

    // Simpan kembali
    await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(list, options));

    return Results.Ok(new { message = "Incident Deleted" });
});
// 5. ANALYZE INCIDENT (Call n8n + Cache + Update JSON Standar Baru)
app.MapPost("/api/incidents/analyze/{id}", async (string id) => {
    if (!IsValidId(id)) return Results.BadRequest("Invalid ID Format");
    var path = Path.Combine(Directory.GetCurrentDirectory(), "data", "incidents.json");
    if (!File.Exists(path)) return Results.NotFound();

    // --- PENTING: GUNAKAN OPTIONS YANG SAMA DENGAN CRUD LAIN ---
    // Agar file JSON tidak berantakan (campur aduk huruf besar/kecil)
    var options = new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, // Wajib agar tersimpan sebagai 'aiAnalysis'
        PropertyNameCaseInsensitive = true // Wajib agar bisa baca 'Id' maupun 'id'
    };

    // 1. Load Data
    var json = await File.ReadAllTextAsync(path);
    var list = System.Text.Json.JsonSerializer.Deserialize<List<Incident>>(json, options) ?? new List<Incident>();

    // Cari Incident
    var incident = list.FirstOrDefault(x => x.Id == id);
    if (incident == null) return Results.NotFound(new { message = "Incident not found" });

    // 2. Cek Cache (Apakah sudah pernah dianalisa?)
    if (!string.IsNullOrEmpty(incident.AiAnalysis))
    {
        return Results.Ok(new { analysis = incident.AiAnalysis, cached = true });
    }

    // 3. Jika Belum, Panggil n8n
    try
    {
        var n8nUrl = "https://n8n.bebekpintar.my.id/webhook/analyze-incident"; // Pastikan URL n8n benar

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(60); // Tambah timeout jaga-jaga AI mikir lama

        // Payload ke n8n
        var payload = new { title = incident.Title, summary = incident.Summary, attacker = incident.Attacker };
        var response = await client.PostAsJsonAsync(n8nUrl, payload);

        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

            // Ambil field "analysis" dari response n8n
            if (result.TryGetProperty("analysis", out var analysisProp))
            {
                string analysisText = analysisProp.GetString();

                // 4. Update Object di Memory
                incident.AiAnalysis = analysisText;

                // 5. Simpan Balik ke JSON (PENTING: Pakai 'options' yang sama!)
                await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(list, options));

                return Results.Ok(new { analysis = analysisText, cached = false });
            }
            else
            {
                return Results.Problem("Field 'analysis' missing in n8n response.");
            }
        }
        else
        {
            return Results.Problem("n8n Error: " + response.StatusCode);
        }
    }
    catch (Exception ex)
    {
        return Results.Problem("System Error: " + ex.Message);
    }
});
// ENDPOINT: UPDATE STATUS (RESOLVE / REOPEN)
app.MapPut("/api/incidents/{id}/status", async (string id, [FromBody] string newStatus, IConfiguration config, HttpContext context) => {
    var path = Path.Combine(Directory.GetCurrentDirectory(), "data", "incidents.json");

    // 1. Validasi Input Status (Biar tidak diisi sembarangan)
    var validStatuses = new[] { "OPEN", "RESOLVED", "INVESTIGATING" };
    if (!validStatuses.Contains(newStatus.ToUpper()))
        return Results.BadRequest("Invalid Status. Use OPEN, RESOLVED, or INVESTIGATING.");

    // 2. Load Data
    if (!File.Exists(path)) return Results.NotFound();

    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    var json = await File.ReadAllTextAsync(path);
    var list = JsonSerializer.Deserialize<List<Incident>>(json, options) ?? new List<Incident>();

    // 3. Cari Incident
    var inc = list.FirstOrDefault(x => x.Id == id);
    if (inc == null) return Results.NotFound(new { message = "Incident ID not found" });

    // 4. Update Status
    inc.Status = newStatus.ToUpper();

    // 5. Simpan
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(list, options));

    return Results.Ok(new { message = $"Status updated to {inc.Status}" });
});
// Buat fungsi validasi sederhana atau Regex
// ENDPOINT: BULK UPDATE STATUS
app.MapPut("/api/incidents/bulk-status", async ([FromBody] BulkUpdateDto req) => {
    var path = Path.Combine(Directory.GetCurrentDirectory(), "data", "incidents.json");

    // Gunakan Semaphore
    await _fileLock.WaitAsync();

    try
    {
        if (!File.Exists(path)) return Results.NotFound();

        var json = await File.ReadAllTextAsync(path);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var list = JsonSerializer.Deserialize<List<Incident>>(json, options) ?? new List<Incident>();

        int updatedCount = 0;

        foreach (var id in req.Ids)
        {
            var inc = list.FirstOrDefault(x => x.Id == id);
            if (inc != null)
            {
                inc.Status = req.Status;
                updatedCount++;
            }
        }

        if (updatedCount > 0)
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(list, options));
        }

        return Results.Ok(new { message = $"{updatedCount} incidents updated to {req.Status}" });
    }
    finally
    {
        _fileLock.Release(); // Selalu lepaskan kunci!
    }
});
bool IsValidId(string id)
{
    // Hanya izinkan Alphanumeric dan tanda strip (-)
    return System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z0-9\-]+$");
}
app.MapHub<ThreatHub>("/threatHub"); // Ini URL yang akan dipanggil geo.html
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
class Incident
{
    public string Id { get; set; }
    public DateTime Date { get; set; }
    public string Title { get; set; }
    public string Severity { get; set; } // LOW, MED, HIGH, CRITICAL
    public string Attacker { get; set; }
    public string Summary { get; set; }
    public string[] Tags { get; set; }
    public string AiAnalysis { get; set; }
    public string Status { get; set; } = "OPEN"; // Default: OPEN
}
public class BulkUpdateDto
{
    public List<string> Ids { get; set; } = new();
    public string Status { get; set; } = "RESOLVED";
}
public class AcunetixHistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Date { get; set; }
    public string TargetUrl { get; set; }
    public string TemplateName { get; set; } // Developer, Executive, dll
    public string Status { get; set; } // processing, ready, failed
    public string DownloadLink { get; set; }
}