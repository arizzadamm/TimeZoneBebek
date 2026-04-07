using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Text;
using TimeZoneBebek.Helpers;

namespace TimeZoneBebek.Controllers
{
    public class PageController : Controller
    {
        [HttpGet("/")]
        public async Task<IActionResult> Dashboard()
        {
            var process = Process.GetCurrentProcess();
            var uptime = DateTime.Now - process.StartTime;
            var osDesc = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

            string diskInfo = "N/A"; string diskClass = ""; int diskPct = 0;
            try
            {
                var d = DriveInfo.GetDrives().FirstOrDefault(x => x.IsReady && (x.Name == "/" || x.Name.StartsWith("C")));
                if (d != null)
                {
                    double t = 1024.0 * 1024.0 * 1024.0;
                    double f = d.AvailableFreeSpace / t;
                    diskPct = 100 - (int)((f / (d.TotalSize / t)) * 100);
                    diskInfo = $"{f:F1} GB Free";
                    if (f < 2) diskClass = "alert";
                }
            }
            catch { }

            string ramInfo = "N/A"; int ramPct = 0;
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    var lines = await System.IO.File.ReadAllLinesAsync("/proc/meminfo");
                    long GetVal(string k) => long.Parse(lines.First(l => l.StartsWith(k)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
                    double t = GetVal("MemTotal:") / 1024.0 / 1024.0;
                    double u = t - (GetVal("MemAvailable:") / 1024.0 / 1024.0);
                    ramInfo = $"{u:F1} / {t:F1} GB"; ramPct = (int)((u / t) * 100);
                }
                catch { }
            }
            else { ramInfo = "App Mode"; ramPct = 10; }

            var headerDump = new StringBuilder();
            foreach (var h in Request.Headers) headerDump.AppendLine($"<div class='row'><span class='key'>{h.Key}:</span> <span class='val'>{h.Value}</span></div>");

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            if (Request.Headers.ContainsKey("CF-Connecting-IP")) ip = Request.Headers["CF-Connecting-IP"];
            else if (Request.Headers.ContainsKey("X-Forwarded-For")) ip = Request.Headers["X-Forwarded-For"];

            string html = await System.IO.File.ReadAllTextAsync("wwwroot/pages/dashboard.html");
            html = html.Replace("{{PRELOADER}}", UIHelpers.GetPreloader())
                       .Replace("{{SIDEBAR}}", UIHelpers.GetSidebar("home"))
                       .Replace("{{OS_DESC}}", osDesc)
                       .Replace("{{RAM_DESC}}", ramInfo).Replace("{{RAM_PCT}}", ramPct.ToString())
                       .Replace("{{DISK_DESC}}", diskInfo).Replace("{{DISK_PCT}}", diskPct.ToString())
                       .Replace("{{DISK_CLASS}}", diskClass)
                       .Replace("{{UPTIME}}", $"{uptime.Days}d {uptime.Hours}h")
                       .Replace("{{REMOTE_IP}}", ip ?? "Unknown").Replace("{{PROTOCOL}}", Request.Protocol)
                       .Replace("{{HEADER_DUMP}}", headerDump.ToString());

            return Content(html, "text/html");
        }

        [HttpGet("/login")]
        public async Task<IActionResult> Login()
        {
            return await RenderNamedPage("login");
        }

        [HttpGet("/{pageName}")]
        public async Task<IActionResult> RenderPage(string pageName)
        {
            return await RenderNamedPage(pageName);
        }

        private async Task<IActionResult> RenderNamedPage(string pageName)
        {
            var validPages = new Dictionary<string, string> {
                { "geo", "geo" }, { "geoold", "geo" }, { "monitor", "monitor" },
                { "reports", "reports" }, { "portal", "portal" }, { "archive", "archive" }, { "nms", "nms" }, { "login", "login" }
            };

            if (!validPages.ContainsKey(pageName)) return NotFound();

            string fileName = pageName == "geoold" ? "geoold.html" : $"{validPages[pageName]}.html";
            string path = Path.Combine(Directory.GetCurrentDirectory(), $"wwwroot/pages/{fileName}");

            if (!System.IO.File.Exists(path)) return NotFound();

            string html = await System.IO.File.ReadAllTextAsync(path);
            html = html.Replace("{{PRELOADER}}", UIHelpers.GetPreloader())
                       .Replace("{{SIDEBAR}}", pageName == "login" ? "" : UIHelpers.GetSidebar(validPages[pageName]));

            return Content(html, "text/html");
        }
    }
}
