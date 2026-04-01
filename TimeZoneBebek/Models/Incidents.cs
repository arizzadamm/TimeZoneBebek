namespace TimeZoneBebek.Models
{
    public static class IncidentStatuses
    {
        public const string New = "NEW";
        public const string Triaged = "TRIAGED";
        public const string InProgress = "IN_PROGRESS";
        public const string Escalated = "ESCALATED";
        public const string Resolved = "RESOLVED";
        public const string FalsePositive = "FALSE_POSITIVE";

        public static readonly string[] All =
        [
            New,
            Triaged,
            InProgress,
            Escalated,
            Resolved,
            FalsePositive
        ];

        private static readonly Dictionary<string, string[]> WorkflowTransitions = new(StringComparer.OrdinalIgnoreCase)
        {
            [New] = [Triaged, InProgress, FalsePositive],
            [Triaged] = [InProgress, Escalated, FalsePositive, Resolved],
            [InProgress] = [Escalated, Resolved, FalsePositive, Triaged],
            [Escalated] = [InProgress, Resolved, FalsePositive],
            [Resolved] = [InProgress],
            [FalsePositive] = [Triaged]
        };

        public static bool IsTerminal(string? status) =>
            string.Equals(status, Resolved, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, FalsePositive, StringComparison.OrdinalIgnoreCase);

        public static IReadOnlyList<string> GetAllowedNextStatuses(string? currentStatus)
        {
            var normalized = Normalize(currentStatus);
            return WorkflowTransitions.TryGetValue(normalized, out var transitions) ? transitions : [];
        }

        public static bool CanTransition(string? fromStatus, string? toStatus)
        {
            var from = Normalize(fromStatus);
            var to = Normalize(toStatus);
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                return true;

            return WorkflowTransitions.TryGetValue(from, out var transitions) &&
                transitions.Contains(to, StringComparer.OrdinalIgnoreCase);
        }

        public static string Normalize(string? status)
        {
            var value = (status ?? "").Trim().ToUpperInvariant();
            return value switch
            {
                "" or "OPEN" => New,
                "INVESTIGATING" => InProgress,
                _ when All.Contains(value) => value,
                _ => New
            };
        }
    }

    public class Incident
    {
        public string? Id { get; set; }
        public DateTime Date { get; set; }
        public string Title { get; set; } = "";
        public string Severity { get; set; } = "LOW";
        public string Attacker { get; set; } = "";
        public string Summary { get; set; } = "";
        public string[]? Tags { get; set; }
        public string? AiAnalysis { get; set; }
        public string Status { get; set; } = IncidentStatuses.New;
        public string? Owner { get; set; }
        public string? Source { get; set; }
        public string? AffectedAsset { get; set; }
        public DateTime? FirstSeen { get; set; }
        public DateTime? LastSeen { get; set; }
        public string? ResolutionNote { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public List<IncidentAuditEntry> AuditTrail { get; set; } = [];
    }

    public class IncidentAuditEntry
    {
        public DateTime TimestampUtc { get; set; }
        public string Action { get; set; } = "";
        public string Actor { get; set; } = "SOC_CONSOLE";
        public string Message { get; set; } = "";
    }

    public class BulkUpdateDto
    {
        public List<string> Ids { get; set; } = new();
        public string Status { get; set; } = IncidentStatuses.Resolved;
    }
}
