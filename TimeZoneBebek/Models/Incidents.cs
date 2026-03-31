namespace TimeZoneBebek.Models
{
    public class Incident
    {
        public string? Id { get; set; }
        public DateTime Date { get; set; }
        public string Title { get; set; }
        public string Severity { get; set; } // LOW, MED, HIGH, CRITICAL
        public string Attacker { get; set; }
        public string Summary { get; set; }
        public string[]? Tags { get; set; }
        public string? AiAnalysis { get; set; }
        public string Status { get; set; } = "OPEN";
    }

    public class BulkUpdateDto
    {
        public List<string> Ids { get; set; } = new();
        public string Status { get; set; } = "RESOLVED";
    }
}
