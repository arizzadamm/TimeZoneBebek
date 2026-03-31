using System.Text.Json;

namespace TimeZoneBebek.Helpers
{
    public static class JsonHelper
    {
        private static readonly JsonSerializerOptions _opts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public static async Task<T> LoadJson<T>(string file) where T : new()
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "data", file);
            if (!File.Exists(path)) return new T();
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<T>(json, _opts) ?? new T();
        }

        public static async Task SaveJson<T>(string file, T data)
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "data", file);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(data, _opts));
        }
    }
}