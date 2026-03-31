using TimeZoneBebek.Models;
using TimeZoneBebek.Repositories;
namespace TimeZoneBebek.Services
{
    public class IncidentService
    {
        private readonly JsonRepository<List<Incident>> _repo;
        private readonly HttpClient _httpClient;

        public IncidentService(HttpClient httpClient)
        {
            _repo = new JsonRepository<List<Incident>>("incidents.json");
            _httpClient = httpClient;
        }

        public async Task<List<Incident>> GetAllAsync()
        {
            return await _repo.LoadAsync();
        }

        public async Task<(bool Success, string Message, string Id)> AddAsync(Incident newInc)
        {
            var list = await _repo.LoadAsync();
            if (newInc.Date == default) newInc.Date = DateTime.Now;

            // Validasi Duplikat
            if (!string.IsNullOrEmpty(newInc.Id) && list.Any(x => x.Id == newInc.Id))
                return (false, $"Duplicate ID: {newInc.Id}", null);

            if (list.Any(x => x.Title.Equals(newInc.Title, StringComparison.OrdinalIgnoreCase) &&
                  x.Attacker.Equals(newInc.Attacker, StringComparison.OrdinalIgnoreCase) &&
                  x.Date == newInc.Date))
            {
                return (false, "Duplicate Content", null);
            }

            // Generate ID Baru
            if (string.IsNullOrEmpty(newInc.Id))
                newInc.Id = "INC-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + "-" + Guid.NewGuid().ToString().Substring(0, 4).ToUpper();

            list.Insert(0, newInc);
            await _repo.SaveAsync(list);

            return (true, "Logged", newInc.Id);
        }

        public async Task<bool> UpdateAsync(string id, Incident updatedInc)
        {
            var list = await _repo.LoadAsync();
            var item = list.FirstOrDefault(x => x.Id == id);
            if (item == null) return false;

            item.Title = updatedInc.Title;
            item.Severity = updatedInc.Severity;
            item.Attacker = updatedInc.Attacker;
            item.Summary = updatedInc.Summary;
            item.Tags = updatedInc.Tags;
            if (!string.IsNullOrEmpty(updatedInc.AiAnalysis)) item.AiAnalysis = updatedInc.AiAnalysis;

            await _repo.SaveAsync(list);
            return true;
        }

        public async Task<bool> DeleteAsync(string id)
        {
            var list = await _repo.LoadAsync();
            var item = list.FirstOrDefault(x => x.Id.Trim() == id.Trim());
            if (item == null) return false;

            list.Remove(item);
            await _repo.SaveAsync(list);
            return true;
        }

        public async Task<bool> UpdateStatusAsync(string id, string newStatus)
        {
            var list = await _repo.LoadAsync();
            var item = list.FirstOrDefault(x => x.Id == id);
            if (item == null) return false;

            item.Status = newStatus.ToUpper();
            await _repo.SaveAsync(list);
            return true;
        }

        public async Task<int> BulkUpdateStatusAsync(BulkUpdateDto req)
        {
            var list = await _repo.LoadAsync();
            int count = 0;
            foreach (var id in req.Ids)
            {
                var item = list.FirstOrDefault(x => x.Id == id);
                if (item != null) { item.Status = req.Status; count++; }
            }
            if (count > 0) await _repo.SaveAsync(list);
            return count;
        }

        public async Task<(bool Success, string Analysis)> AnalyzeWithAiAsync(string id)
        {
            var list = await _repo.LoadAsync();
            var item = list.FirstOrDefault(x => x.Id == id);
            if (item == null) return (false, "Incident not found");
            if (!string.IsNullOrEmpty(item.AiAnalysis)) return (true, item.AiAnalysis); // Cached

            try
            {
                _httpClient.Timeout = TimeSpan.FromSeconds(60);
                var payload = new { title = item.Title, summary = item.Summary, attacker = item.Attacker };
                var response = await _httpClient.PostAsJsonAsync("https://n8n.bebekpintar.my.id/webhook/analyze-incident", payload);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                    if (result.TryGetProperty("analysis", out var analysisProp))
                    {
                        item.AiAnalysis = analysisProp.GetString();
                        await _repo.SaveAsync(list);
                        return (true, item.AiAnalysis);
                    }
                }
                return (false, "n8n AI Error");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}
