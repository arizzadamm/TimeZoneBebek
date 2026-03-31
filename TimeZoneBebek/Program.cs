using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using TimeZoneBebek.Hubs;
using TimeZoneBebek.Services; // Termasuk IncidentService yang kita buat sebelumnya

var builder = WebApplication.CreateBuilder(args);

// ==========================================
// 1. REGISTRASI SERVICES (DI Container)
// ==========================================
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

builder.Services.AddHttpClient();
builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddScoped<IncidentService>(); 
builder.Services.AddHostedService<ElasticWorker>();
builder.Services.AddSingleton<TimeZoneBebek.Services.ElasticEpsService>();

// Rate Limiting (Security)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
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

var app = builder.Build();

// ==========================================
// 2. MIDDLEWARE PIPELINE
// ==========================================
app.UseCors("AllowAll");

// Middleware Security & API Key
app.Use(async (context, next) =>
{
    if (context.Request.Method.ToUpper() == "OPTIONS" || !context.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }

    var headerKey = context.Request.Headers.Keys.FirstOrDefault(k => k.Equals("X-API-KEY", StringComparison.OrdinalIgnoreCase));
    if (headerKey == null || !context.Request.Headers.TryGetValue(headerKey, out var extractedApiKey))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { message = "Access Denied: Missing API Key" });
        return;
    }

    var configuredApiKey = app.Configuration["Authentication:ApiKey"];
    if (string.IsNullOrEmpty(configuredApiKey) || !string.Equals(configuredApiKey, extractedApiKey.ToString().Trim()))
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { message = "Access Denied: Invalid API Key" });
        return;
    }

    var headers = context.Response.Headers;
    headers.Append("X-Frame-Options", "DENY");
    headers.Append("X-Content-Type-Options", "nosniff");
    headers.Append("X-XSS-Protection", "1; mode=block");
    headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    headers.Append("Content-Security-Policy",
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' https://unpkg.com https://cdn.jsdelivr.net https://api.aladhan.com https://cdnjs.cloudflare.com/ajax/libs/html2pdf.js/0.10.1/html2pdf.bundle.min.js; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data: https://tile.openstreetmap.org https://unpkg.com https://cdn.jsdelivr.net; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://unpkg.com https://cdn.jsdelivr.net; " +
        "connect-src 'self' http://ip-api.com https://feeds.feedburner.com https://api.aladhan.com https://cdn.jsdelivr.net https://cdnjs.cloudflare.com/ajax/libs/html2pdf.js/0.10.1/html2pdf.bundle.min.js;");

    await next();
});

app.UseStaticFiles();
app.UseRouting(); // Wajib ada untuk Controller
app.UseRateLimiter();

// ==========================================
// 3. MAPPING ROUTES
// ==========================================
app.MapControllers(); // Semua route API dan Page otomatis ditangani oleh Controller
app.MapHub<ThreatHub>("/threatHub");

app.Run();