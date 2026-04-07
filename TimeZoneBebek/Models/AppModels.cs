namespace TimeZoneBebek.Models
{
    public class ServiceConfig { public string Name { get; set; } = ""; public string Host { get; set; } = ""; public int Port { get; set; } public string Type { get; set; } = ""; }
    public class NewsItem { public string Title { get; set; } = ""; public string Link { get; set; } = ""; }
    public class ElasticConfig { public string Url { get; set; } = ""; public string Username { get; set; } = ""; public string Password { get; set; } = ""; public string IndexPattern { get; set; } = ""; public bool AllowInvalidCertificate { get; set; } }
    public class ThreatData { public string Ip { get; set; } = ""; public int Count { get; set; } public double Lat { get; set; } public double Lon { get; set; } public string Country { get; set; } = ""; }
    public class PortalAppConfig { public string Name { get; set; } = ""; public string Url { get; set; } = ""; public string Description { get; set; } = ""; public string Category { get; set; } = ""; }
    public class AcunetixHistoryItem { public string Id { get; set; } = Guid.NewGuid().ToString(); public string Date { get; set; } = ""; public string TargetUrl { get; set; } = ""; public string TemplateName { get; set; } = ""; public string Status { get; set; } = ""; public string DownloadLink { get; set; } = ""; public string Environment { get; set; } = ""; }
    public class OperationsConfig { public string IncidentAiWebhookUrl { get; set; } = ""; public int SessionDurationHours { get; set; } = 8; }
}
