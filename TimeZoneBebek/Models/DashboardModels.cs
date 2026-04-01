namespace TimeZoneBebek.Models
{
    public class DashboardSummary
    {
        public int OpenTickets { get; set; }
        public int CriticalOpenTickets { get; set; }
        public int HighOpenTickets { get; set; }
        public string DefconLevel { get; set; } = "DEFCON 5";
        public string DefconDesc { get; set; } = "NORMAL OPERATIONS";
        public string DefconColor { get; set; } = "var(--green)";
        public string DefconShadow { get; set; } = "none";
        public string TopAttacker { get; set; } = "---";
        public double TopAttackerPct { get; set; }
        public List<Incident> RecentIncidents { get; set; } = [];
        public List<int> IncidentTrend { get; set; } = [];
        public Dictionary<string, int> StatusCounts { get; set; } = [];
        public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public class DashboardHealth
    {
        public bool ElasticHealthy { get; set; }
        public bool EpsHealthy { get; set; }
        public bool ThreatWebhookHealthy { get; set; }
        public bool FeedHealthy { get; set; }
        public DateTime? LastElasticSuccessUtc { get; set; }
        public DateTime? LastBroadcastUtc { get; set; }
        public DateTime? LastWebhookSuccessUtc { get; set; }
        public DateTime? LastEpsSuccessUtc { get; set; }
        public string? LastElasticError { get; set; }
        public string? LastWebhookError { get; set; }
        public int LastKnownEventsPerSecond { get; set; }
        public int LastKnownEventsPerMinute { get; set; }
    }

    public class EpsSnapshot
    {
        public int EventsPerSecond { get; set; }
        public int EventsLastMinute { get; set; }
        public DateTime CapturedAtUtc { get; set; }
    }

    public class AuthSessionInfo
    {
        public bool Valid { get; set; }
        public int SessionDurationHours { get; set; }
        public DateTime ServerTimeUtc { get; set; }
    }
}
