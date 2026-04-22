using TimeZoneBebek.Models;

namespace TimeZoneBebek.Services
{
    public class IncidentService
    {
        private static readonly string[] AllowedSeverities = ["LOW", "MEDIUM", "HIGH", "CRITICAL"];
        private const string DefaultActor = "SOC_CONSOLE";

        private readonly IIncidentStore _store;
        private readonly HttpClient _httpClient;
        private readonly OperationsConfig _operationsConfig;

        public IncidentService(IIncidentStore store, HttpClient httpClient, IConfiguration configuration)
        {
            _store = store;
            _httpClient = httpClient;
            _operationsConfig = configuration.GetSection("OperationsConfig").Get<OperationsConfig>() ?? new OperationsConfig();
        }

        public async Task<List<Incident>> GetAllAsync()
        {
            var incidents = await _store.LoadAsync();
            return incidents
                .Select(NormalizeLoadedIncident)
                .OrderByDescending(i => i.Date)
                .ToList();
        }

        public async Task<IncidentArchivePage> SearchArchiveAsync(IncidentArchiveQuery query)
        {
            var normalizedQuery = new IncidentArchiveQuery
            {
                Search = query.Search,
                Severity = string.IsNullOrWhiteSpace(query.Severity) ? "ALL" : query.Severity.Trim().ToUpperInvariant(),
                Status = string.IsNullOrWhiteSpace(query.Status) ? "ALL" : query.Status.Trim().ToUpperInvariant(),
                Page = Math.Max(query.Page, 1),
                PageSize = Math.Clamp(query.PageSize, 10, 100)
            };

            var page = await _store.SearchAsync(normalizedQuery);
            page.Items = page.Items
                .Select(NormalizeLoadedIncident)
                .ToList();
            return page;
        }

        public async Task<DashboardSummary> GetDashboardSummaryAsync()
        {
            var data = await GetAllAsync();
            var active = data.Where(i => !IncidentStatuses.IsTerminal(i.Status)).ToList();
            var critical = active.Count(i => i.Severity == "CRITICAL");
            var high = active.Count(i => i.Severity == "HIGH");

            var summary = new DashboardSummary
            {
                OpenTickets = active.Count,
                CriticalOpenTickets = critical,
                HighOpenTickets = high,
                RecentIncidents = data
                    .OrderByDescending(GetPriorityScore)
                    .ThenByDescending(i => i.Date)
                    .Take(8)
                    .ToList(),
                StatusCounts = data
                    .GroupBy(i => i.Status)
                    .ToDictionary(g => g.Key, g => g.Count()),
                IncidentTrend = BuildTrendSeries(data)
            };

            if (critical > 0)
            {
                summary.DefconLevel = "DEFCON 1";
                summary.DefconDesc = "CRITICAL THREAT ACTIVE";
                summary.DefconColor = "var(--red)";
                summary.DefconShadow = "0 0 15px rgba(255,0,0,0.3)";
            }
            else if (high > 2)
            {
                summary.DefconLevel = "DEFCON 3";
                summary.DefconDesc = "ELEVATED RISK";
                summary.DefconColor = "#e67e22";
                summary.DefconShadow = "none";
            }

            var counts = data
                .Where(i => !string.IsNullOrWhiteSpace(i.Attacker))
                .GroupBy(i => i.Attacker)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();

            if (counts != null)
            {
                summary.TopAttacker = counts.Key;
                summary.TopAttackerPct = Math.Min((double)counts.Count() / Math.Max(data.Count, 1) * 100, 100);
            }

            return summary;
        }

        public object GetWorkflowMetadata() => new
        {
            statuses = IncidentStatuses.All,
            transitions = IncidentStatuses.All.ToDictionary(
                status => status,
                status => IncidentStatuses.GetAllowedNextStatuses(status))
        };

        public async Task<(bool Success, string Message, string? Id)> AddAsync(Incident newInc)
        {
            var validation = ValidateAndNormalize(newInc, isCreate: true);
            if (!validation.Success)
                return (false, validation.Message, null);

            var normalized = validation.Incident!;
            var list = await _store.LoadAsync();
            if (!string.IsNullOrEmpty(normalized.Id) && list.Any(x => x.Id == normalized.Id))
                return (false, $"Duplicate ID: {normalized.Id}", null);

            if (list.Any(x =>
                x.Title.Equals(normalized.Title, StringComparison.OrdinalIgnoreCase) &&
                x.Attacker.Equals(normalized.Attacker, StringComparison.OrdinalIgnoreCase) &&
                x.Date == normalized.Date))
            {
                return (false, "Duplicate Content", null);
            }

            normalized.Id ??= "INC-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + "-" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
            normalized.UpdatedAt = DateTime.UtcNow;
            AddAudit(normalized, "CREATED", $"Incident created with status {normalized.Status} and severity {normalized.Severity}");
            if (!string.IsNullOrWhiteSpace(normalized.Owner))
                AddAudit(normalized, "ASSIGNED", $"Assigned to {normalized.Owner}");

            await _store.AddAsync(normalized);
            return (true, "Logged", normalized.Id);
        }

        public async Task<(bool Success, string Message)> UpdateAsync(string id, Incident updatedInc)
        {
            var item = await _store.GetByIdAsync(id);
            if (item == null)
                return (false, "Incident Not Found");

            updatedInc.Id = item.Id;
            updatedInc.Date = item.Date;
            updatedInc.AiAnalysis ??= item.AiAnalysis;
            updatedInc.AuditTrail = item.AuditTrail;

            var validation = ValidateAndNormalize(updatedInc, isCreate: false);
            if (!validation.Success)
                return (false, validation.Message);

            var normalized = validation.Incident!;
            if (!IncidentStatuses.CanTransition(item.Status, normalized.Status))
                return (false, $"Invalid status transition {item.Status} -> {normalized.Status}");

            TrackFieldChanges(item, normalized);

            item.Title = normalized.Title;
            item.Severity = normalized.Severity;
            item.Attacker = normalized.Attacker;
            item.Summary = normalized.Summary;
            item.Tags = normalized.Tags;
            item.AiAnalysis = normalized.AiAnalysis;
            item.Status = normalized.Status;
            item.Owner = normalized.Owner;
            item.Source = normalized.Source;
            item.AffectedAsset = normalized.AffectedAsset;
            item.FirstSeen = normalized.FirstSeen;
            item.LastSeen = normalized.LastSeen;
            item.ResolutionNote = normalized.ResolutionNote;
            item.UpdatedAt = DateTime.UtcNow;

            await _store.UpdateAsync(item);
            return (true, "Incident Updated");
        }

        public async Task<bool> DeleteAsync(string id)
        {
            return await _store.DeleteAsync(id);
        }

        public async Task<(bool Success, string Message)> UpdateStatusAsync(string id, string newStatus)
        {
            var normalizedStatus = IncidentStatuses.Normalize(newStatus);
            if (!IncidentStatuses.All.Contains(normalizedStatus))
                return (false, "Invalid Status");

            var item = await _store.GetByIdAsync(id);
            if (item == null)
                return (false, "Incident Not Found");
            if (!IncidentStatuses.CanTransition(item.Status, normalizedStatus))
                return (false, $"Invalid status transition {item.Status} -> {normalizedStatus}");

            var previous = item.Status;
            item.Status = normalizedStatus;
            item.UpdatedAt = DateTime.UtcNow;
            if (IncidentStatuses.IsTerminal(item.Status) && string.IsNullOrWhiteSpace(item.ResolutionNote))
                item.ResolutionNote = $"Marked as {item.Status} on {DateTime.Now:yyyy-MM-dd HH:mm}";

            AddAudit(item, "STATUS_CHANGED", $"Status changed from {previous} to {item.Status}");
            await _store.UpdateAsync(item);
            return (true, "Status Updated");
        }

        public async Task<int> BulkUpdateStatusAsync(BulkUpdateDto req)
        {
            var status = IncidentStatuses.Normalize(req.Status);
            if (!IncidentStatuses.All.Contains(status))
                return 0;

            return await _store.BulkUpdateStatusAsync(
                req.Ids,
                status,
                item => IncidentStatuses.CanTransition(item.Status, status),
                item =>
                {
                    var previous = item.Status;
                    item.Status = status;
                    item.UpdatedAt = DateTime.UtcNow;
                    if (IncidentStatuses.IsTerminal(status) && string.IsNullOrWhiteSpace(item.ResolutionNote))
                        item.ResolutionNote = $"Bulk updated to {status} on {DateTime.Now:yyyy-MM-dd HH:mm}";

                    AddAudit(item, "STATUS_CHANGED", $"Status changed from {previous} to {status} via bulk update");
                    return item;
                });
        }

        public async Task<(bool Success, string Analysis)> AnalyzeWithAiAsync(string id)
        {
            var item = await _store.GetByIdAsync(id);
            if (item == null)
                return (false, "Incident not found");
            if (!string.IsNullOrEmpty(item.AiAnalysis))
                return (true, item.AiAnalysis);
            if (string.IsNullOrWhiteSpace(_operationsConfig.IncidentAiWebhookUrl))
                return (false, "Incident AI webhook URL is not configured");

            try
            {
                _httpClient.Timeout = TimeSpan.FromSeconds(60);
                var payload = new
                {
                    title = item.Title,
                    summary = item.Summary,
                    attacker = item.Attacker,
                    severity = item.Severity,
                    source = item.Source,
                    affectedAsset = item.AffectedAsset
                };

                var response = await _httpClient.PostAsJsonAsync(_operationsConfig.IncidentAiWebhookUrl, payload);
                if (!response.IsSuccessStatusCode)
                    return (false, "n8n AI Error");

                var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                if (!result.TryGetProperty("analysis", out var analysisProp))
                    return (false, "n8n AI Error");

                item.AiAnalysis = analysisProp.GetString();
                item.UpdatedAt = DateTime.UtcNow;
                AddAudit(item, "AI_ANALYSIS", "AI threat analysis generated");
                await _store.UpdateAsync(item);
                return (true, item.AiAnalysis ?? "");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static Incident NormalizeLoadedIncident(Incident incident)
        {
            incident.Title = (incident.Title ?? "").Trim();
            incident.Severity = NormalizeSeverity(incident.Severity);
            incident.Attacker = (incident.Attacker ?? "").Trim();
            incident.Summary = (incident.Summary ?? "").Trim();
            incident.Tags = incident.Tags?
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            incident.Status = IncidentStatuses.Normalize(incident.Status);
            incident.Owner = NormalizeOptional(incident.Owner);
            incident.Source = NormalizeOptional(incident.Source);
            incident.AffectedAsset = NormalizeOptional(incident.AffectedAsset);
            incident.ResolutionNote = NormalizeOptional(incident.ResolutionNote);
            if (IsWafBlockedStatusCode(incident.HttpStatusCode))
                incident.Status = IncidentStatuses.FalsePositive;
            incident.FirstSeen ??= incident.Date;
            incident.LastSeen ??= incident.Date;
            incident.AuditTrail ??= [];
            return incident;
        }

        private static (bool Success, string Message, Incident? Incident) ValidateAndNormalize(Incident incident, bool isCreate)
        {
            var normalized = NormalizeLoadedIncident(incident);

            if (string.IsNullOrWhiteSpace(normalized.Title))
                return (false, "Title is required", null);
            if (normalized.Title.Length > 140)
                return (false, "Title is too long", null);
            if (string.IsNullOrWhiteSpace(normalized.Attacker))
                return (false, "Attacker is required", null);
            if (normalized.Attacker.Length > 120)
                return (false, "Attacker is too long", null);
            if (string.IsNullOrWhiteSpace(normalized.Summary))
                return (false, "Summary is required", null);
            if (normalized.Summary.Length > 2000)
                return (false, "Summary is too long", null);
            if (!AllowedSeverities.Contains(normalized.Severity))
                return (false, "Severity is invalid", null);
            if (!IncidentStatuses.All.Contains(normalized.Status))
                return (false, "Status is invalid", null);

            if (normalized.Date == default)
                normalized.Date = DateTime.Now;

            normalized.FirstSeen ??= normalized.Date;
            normalized.LastSeen ??= normalized.Date;
            normalized.UpdatedAt = DateTime.UtcNow;
            if (isCreate && string.IsNullOrWhiteSpace(normalized.Status))
                normalized.Status = IncidentStatuses.New;

            return (true, "", normalized);
        }

        private static string NormalizeSeverity(string? severity)
        {
            var value = (severity ?? "").Trim().ToUpperInvariant();
            return value switch
            {
                "MED" => "MEDIUM",
                "" => "LOW",
                _ => value
            };
        }

        private static string? NormalizeOptional(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static int GetPriorityScore(Incident incident)
        {
            var severityScore = incident.Severity switch
            {
                "CRITICAL" => 400,
                "HIGH" => 300,
                "MEDIUM" => 200,
                _ => 100
            };

            var statusScore = incident.Status switch
            {
                IncidentStatuses.Escalated => 60,
                IncidentStatuses.InProgress => 45,
                IncidentStatuses.Triaged => 30,
                IncidentStatuses.New => 15,
                _ => 0
            };

            return severityScore + statusScore;
        }

        private static bool IsWafBlockedStatusCode(int? statusCode) =>
            statusCode.HasValue && statusCode.Value > 0 && statusCode.Value != 200;

        private static List<int> BuildTrendSeries(List<Incident> incidents)
        {
            var trend = new int[12];
            var now = DateTime.Now;

            foreach (var incident in incidents)
            {
                var hoursAgo = (now - incident.Date).TotalHours;
                if (hoursAgo < 0 || hoursAgo > 12)
                    continue;

                trend[(int)Math.Floor(hoursAgo)]++;
            }

            return trend.Reverse().ToList();
        }

        private static void AddAudit(Incident incident, string action, string message, string actor = DefaultActor)
        {
            incident.AuditTrail ??= [];
            incident.AuditTrail.Insert(0, new IncidentAuditEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Action = action,
                Actor = actor,
                Message = message
            });

            if (incident.AuditTrail.Count > 50)
                incident.AuditTrail = incident.AuditTrail.Take(50).ToList();
        }

        private static void TrackFieldChanges(Incident current, Incident updated)
        {
            if (!string.Equals(current.Status, updated.Status, StringComparison.OrdinalIgnoreCase))
                AddAudit(current, "STATUS_CHANGED", $"Status changed from {current.Status} to {updated.Status}");

            if (!string.Equals(current.Owner, updated.Owner, StringComparison.OrdinalIgnoreCase))
            {
                var nextOwner = string.IsNullOrWhiteSpace(updated.Owner) ? "UNASSIGNED" : updated.Owner;
                AddAudit(current, "ASSIGNED", $"Owner updated to {nextOwner}");
            }

            var changedFields = new List<string>();
            if (!string.Equals(current.Title, updated.Title, StringComparison.Ordinal)) changedFields.Add("title");
            if (!string.Equals(current.Severity, updated.Severity, StringComparison.Ordinal)) changedFields.Add("severity");
            if (!string.Equals(current.Attacker, updated.Attacker, StringComparison.Ordinal)) changedFields.Add("attacker");
            if (!string.Equals(current.Summary, updated.Summary, StringComparison.Ordinal)) changedFields.Add("summary");
            if (!string.Equals(current.Source, updated.Source, StringComparison.Ordinal)) changedFields.Add("source");
            if (!string.Equals(current.AffectedAsset, updated.AffectedAsset, StringComparison.Ordinal)) changedFields.Add("affected asset");
            if (!Enumerable.SequenceEqual(current.Tags ?? [], updated.Tags ?? [], StringComparer.OrdinalIgnoreCase)) changedFields.Add("tags");
            if (!string.Equals(current.ResolutionNote, updated.ResolutionNote, StringComparison.Ordinal)) changedFields.Add("resolution note");

            if (changedFields.Count > 0)
                AddAudit(current, "UPDATED", $"Updated {string.Join(", ", changedFields)}");
        }
    }
}
