using TimeZoneBebek.Models;

namespace TimeZoneBebek.Services
{
    public interface IIncidentStore
    {
        Task<List<Incident>> LoadAsync();
        Task SaveAsync(List<Incident> incidents);
        Task<Incident?> GetByIdAsync(string id);
        Task AddAsync(Incident incident);
        Task UpdateAsync(Incident incident);
        Task<bool> DeleteAsync(string id);
        Task<int> BulkUpdateStatusAsync(IEnumerable<string> ids, string status, Func<Incident, bool> canTransition, Func<Incident, Incident> applyUpdate);
        Task<IncidentArchivePage> SearchAsync(IncidentArchiveQuery query);
    }
}
