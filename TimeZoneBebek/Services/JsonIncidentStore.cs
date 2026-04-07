using TimeZoneBebek.Models;
using TimeZoneBebek.Repositories;

namespace TimeZoneBebek.Services
{
    public class JsonIncidentStore : IIncidentStore
    {
        private readonly JsonRepository<List<Incident>> _repo = new("incidents.json");

        public Task<List<Incident>> LoadAsync() => _repo.LoadAsync();

        public Task SaveAsync(List<Incident> incidents) => _repo.SaveAsync(incidents);

        public async Task<Incident?> GetByIdAsync(string id)
        {
            var incidents = await _repo.LoadAsync();
            return incidents.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public async Task AddAsync(Incident incident)
        {
            var incidents = await _repo.LoadAsync();
            incidents.Insert(0, incident);
            await _repo.SaveAsync(incidents);
        }

        public async Task UpdateAsync(Incident incident)
        {
            var incidents = await _repo.LoadAsync();
            var index = incidents.FindIndex(i => string.Equals(i.Id, incident.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                incidents[index] = incident;
            await _repo.SaveAsync(incidents);
        }

        public async Task<bool> DeleteAsync(string id)
        {
            var incidents = await _repo.LoadAsync();
            var removed = incidents.RemoveAll(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
                await _repo.SaveAsync(incidents);

            return removed > 0;
        }

        public async Task<int> BulkUpdateStatusAsync(IEnumerable<string> ids, string status, Func<Incident, bool> canTransition, Func<Incident, Incident> applyUpdate)
        {
            var incidents = await _repo.LoadAsync();
            var idSet = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var updatedCount = 0;

            for (var index = 0; index < incidents.Count; index++)
            {
                var incident = incidents[index];
                if (incident.Id == null || !idSet.Contains(incident.Id) || !canTransition(incident))
                    continue;

                incidents[index] = applyUpdate(incident);
                updatedCount++;
            }

            if (updatedCount > 0)
                await _repo.SaveAsync(incidents);

            return updatedCount;
        }

        public async Task<IncidentArchivePage> SearchAsync(IncidentArchiveQuery query)
        {
            var incidents = await _repo.LoadAsync();
            var normalized = incidents
                .Select(NormalizeArchiveIncident)
                .OrderByDescending(i => i.Date)
                .ToList();

            var filtered = ApplyArchiveFilter(normalized, query);
            var page = Math.Max(query.Page, 1);
            var pageSize = Math.Clamp(query.PageSize, 10, 100);
            var totalCount = filtered.Count;
            var items = filtered
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return new IncidentArchivePage
            {
                Items = items,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
                TotalPages = Math.Max((int)Math.Ceiling(totalCount / (double)pageSize), 1),
                Summary = new IncidentArchiveSummary
                {
                    OpenCount = normalized.Count(i => !IncidentStatuses.IsTerminal(i.Status)),
                    CriticalCount = normalized.Count(i => i.Severity == "CRITICAL" && !IncidentStatuses.IsTerminal(i.Status)),
                    AssignedCount = normalized.Count(i => !string.IsNullOrWhiteSpace(i.Owner)),
                    ResolvedCount = normalized.Count(i => i.Status == IncidentStatuses.Resolved),
                    TotalCount = normalized.Count,
                    FilteredCount = totalCount
                }
            };
        }

        private static List<Incident> ApplyArchiveFilter(List<Incident> incidents, IncidentArchiveQuery query)
        {
            var search = (query.Search ?? "").Trim().ToLowerInvariant();
            var severity = (query.Severity ?? "ALL").Trim().ToUpperInvariant();
            var status = (query.Status ?? "ALL").Trim().ToUpperInvariant();

            return incidents.Where(inc =>
            {
                var searchableParts = new[] { inc.Title, inc.Attacker, inc.Id, inc.Owner, inc.AffectedAsset, inc.Source };
                var searchable = string.Join(" ", searchableParts.Where(v => !string.IsNullOrWhiteSpace(v))).ToLowerInvariant();
                var matchSearch = string.IsNullOrWhiteSpace(search) || searchable.Contains(search);
                var matchSeverity = severity == "ALL" || string.Equals(inc.Severity, severity, StringComparison.OrdinalIgnoreCase);
                var matchStatus = status == "ALL" || string.Equals(inc.Status, status, StringComparison.OrdinalIgnoreCase);
                return matchSearch && matchSeverity && matchStatus;
            }).ToList();
        }

        private static Incident NormalizeArchiveIncident(Incident incident)
        {
            incident.Title = (incident.Title ?? "").Trim();
            incident.Severity = (incident.Severity ?? "LOW").Trim().ToUpperInvariant();
            incident.Attacker = (incident.Attacker ?? "").Trim();
            incident.Summary = (incident.Summary ?? "").Trim();
            incident.Status = IncidentStatuses.Normalize(incident.Status);
            incident.Owner = string.IsNullOrWhiteSpace(incident.Owner) ? null : incident.Owner.Trim();
            incident.Source = string.IsNullOrWhiteSpace(incident.Source) ? null : incident.Source.Trim();
            incident.AffectedAsset = string.IsNullOrWhiteSpace(incident.AffectedAsset) ? null : incident.AffectedAsset.Trim();
            return incident;
        }
    }
}
