using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();

// Load Config
var monitoredServices = builder.Configuration.GetSection("MonitoredServices").Get<List<ServiceConfig>>() ?? new List<ServiceConfig>();

var app = builder.Build();

app.UseStaticFiles(); // Enable wwwroot

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
        <a href='/geo' class='{(active == "geo" ? "active" : "")}'>GEO TRACER</a>
        <a href='/monitor' class='{(active == "monitor" ? "active" : "")}'>SERVICE MONITOR</a>
        //<a href='/latency' class='{(active == "latency" ? "active" : "")}'>LATENCY GRAPH</a>
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

    string diskInfo = "N/A"; string diskClass = "";
    try { var d = DriveInfo.GetDrives().FirstOrDefault(x => x.IsReady && (x.Name == "/" || x.Name.StartsWith("C"))); if (d != null) { double t = 1024.0 * 1024.0 * 1024.0; double f = d.AvailableFreeSpace / t; double tot = d.TotalSize / t; int p = (int)((f / tot) * 100); diskInfo = $"{f:F1} GB / {tot:F1} GB ({p}% Free)"; if (p < 10) diskClass = "alert"; } } catch { }

    string ramInfo = "N/A";
    if (OperatingSystem.IsLinux())
    {
        try { var lines = await File.ReadAllLinesAsync("/proc/meminfo"); long GetVal(string k) => long.Parse(lines.First(l => l.StartsWith(k)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]); double t = GetVal("MemTotal:") / 1024.0 / 1024.0; double u = t - (GetVal("MemAvailable:") / 1024.0 / 1024.0); ramInfo = $"{u:F1} / {t:F1} GB"; } catch { }
    }
    else { ramInfo = $"{process.WorkingSet64 / 1024 / 1024} MB (App)"; }

    var headerDump = new StringBuilder(); foreach (var h in context.Request.Headers) headerDump.AppendLine($"<div class='row'><span class='key'>{h.Key}:</span> <span class='val'>{h.Value}</span></div>");

    var ip = context.Connection.RemoteIpAddress?.ToString();
    if (context.Request.Headers.ContainsKey("CF-Connecting-IP")) ip = context.Request.Headers["CF-Connecting-IP"];
    else if (context.Request.Headers.ContainsKey("X-Forwarded-For")) ip = context.Request.Headers["X-Forwarded-For"];

    string html = await File.ReadAllTextAsync("wwwroot/pages/dashboard.html");
    html = html.Replace("{{PRELOADER}}", GetPreloader()).Replace("{{SIDEBAR}}", GetSidebar("home"))
               .Replace("{{OS_DESC}}", osDesc).Replace("{{RAM_DESC}}", ramInfo)
               .Replace("{{DISK_DESC}}", diskInfo).Replace("{{DISK_CLASS}}", diskClass)
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

app.MapGet("/api/pings", async () => {
    long PingHost(string h) { try { using var p = new Ping(); var r = p.Send(h, 1000); return r.Status == IPStatus.Success ? r.RoundtripTime : -1; } catch { return -1; } }
    var t1 = Task.Run(() => PingHost("1.1.1.1")); var t2 = Task.Run(() => PingHost("8.8.8.8")); await Task.WhenAll(t1, t2);
    return Results.Ok(new { cf = t1.Result, go = t2.Result });
});

app.MapGet("/api/geo-trace", async (HttpContext context, IHttpClientFactory f) => {
    string ip = context.Connection.RemoteIpAddress?.ToString() ?? "";
    if (context.Request.Headers.ContainsKey("CF-Connecting-IP")) ip = context.Request.Headers["CF-Connecting-IP"];
    else if (context.Request.Headers.ContainsKey("X-Forwarded-For")) ip = context.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0];
    if (ip == "::1" || ip == "127.0.0.1") ip = "";

    try
    {
        var client = f.CreateClient();
        var url = string.IsNullOrEmpty(ip) ? "http://ip-api.com/json/" : $"http://ip-api.com/json/{ip}";
        var json = await client.GetStringAsync(url);
        return Results.Content(json, "application/json");
    }
    catch { return Results.Problem(); }
});

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

app.Run();

class ServiceConfig { public string Name { get; set; } = ""; public string Host { get; set; } = ""; public int Port { get; set; } public string Type { get; set; } = ""; }
class NewsItem { public string Title { get; set; } = ""; public string Link { get; set; } = ""; }