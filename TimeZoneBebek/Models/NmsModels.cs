namespace TimeZoneBebek.Models
{
    public static class NmsStatuses
    {
        public const string Up = "UP";
        public const string Down = "DOWN";
        public const string Degraded = "DEGRADED";
        public const string Unknown = "UNKNOWN";
    }

    public class NmsMonitoringConfig
    {
        public int RefreshIntervalSeconds { get; set; } = 15;
        public List<NmsItemConfig> Items { get; set; } = [];
    }

    public class NmsItemConfig
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string CheckType { get; set; } = "ping";
        public string Description { get; set; } = "";
        public string Unit { get; set; } = "";
        public string Value { get; set; } = "";
        public int TimeoutMs { get; set; } = 2500;
        public List<NmsTargetConfig> Targets { get; set; } = [];
    }

    public class NmsTargetConfig
    {
        public string Name { get; set; } = "";
        public string CheckType { get; set; } = "";
        public string Host { get; set; } = "";
        public int? Port { get; set; }
        public string Url { get; set; } = "";
        public int TimeoutMs { get; set; } = 2500;
    }

    public class NmsStatusSnapshot
    {
        public DateTime GeneratedAtUtc { get; set; }
        public int RefreshIntervalSeconds { get; set; } = 15;
        public NmsSummary Summary { get; set; } = new();
        public List<NmsCategoryStatus> Categories { get; set; } = [];
    }

    public class NmsHistoryResponse
    {
        public string Range { get; set; } = "5m";
        public DateTime GeneratedAtUtc { get; set; }
        public List<NmsHistoryPoint> Points { get; set; } = [];
    }

    public class NmsHistoryPoint
    {
        public DateTime GeneratedAtUtc { get; set; }
        public int UpCount { get; set; }
        public int DownCount { get; set; }
        public int DegradedCount { get; set; }
        public int UnknownCount { get; set; }
        public long? AverageLatencyMs { get; set; }
        public List<NmsHistoryCategoryPoint> Categories { get; set; } = [];
        public List<NmsHistoryDownTarget> DownTargets { get; set; } = [];
    }

    public class NmsHistoryCategoryPoint
    {
        public string Name { get; set; } = "";
        public int UpCount { get; set; }
        public int DownCount { get; set; }
        public int DegradedCount { get; set; }
        public int UnknownCount { get; set; }
    }

    public class NmsHistoryDownTarget
    {
        public string ItemName { get; set; } = "";
        public string TargetName { get; set; } = "";
        public string TargetAddress { get; set; } = "";
        public string Detail { get; set; } = "";
        public DateTime SeenAtUtc { get; set; }
    }

    public class NmsSummary
    {
        public int TotalItems { get; set; }
        public int UpCount { get; set; }
        public int DownCount { get; set; }
        public int DegradedCount { get; set; }
        public int UnknownCount { get; set; }
    }

    public class NmsCategoryStatus
    {
        public string Name { get; set; } = "";
        public List<NmsItemStatus> Items { get; set; } = [];
    }

    public class NmsItemStatus
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string Description { get; set; } = "";
        public string Status { get; set; } = NmsStatuses.Unknown;
        public string Detail { get; set; } = "";
        public string CheckType { get; set; } = "";
        public string Unit { get; set; } = "";
        public string Value { get; set; } = "";
        public long? LatencyMs { get; set; }
        public DateTime CheckedAtUtc { get; set; }
        public List<NmsTargetStatus> Targets { get; set; } = [];
    }

    public class NmsTargetStatus
    {
        public string Name { get; set; } = "";
        public string Status { get; set; } = NmsStatuses.Unknown;
        public string Detail { get; set; } = "";
        public string Target { get; set; } = "";
        public string CheckType { get; set; } = "";
        public long? LatencyMs { get; set; }
    }
}
