using System.Text.Json;

namespace TimeZoneBebek.Repositories

{
    public class JsonRepository<T> where T : new()
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _fileLock = new(1, 1);
        private readonly JsonSerializerOptions _opts;

        public JsonRepository(string fileName)
        {
            _filePath = Path.Combine(Directory.GetCurrentDirectory(), "data", fileName);
            _opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
        }

        public async Task<T> LoadAsync()
        {
            await _fileLock.WaitAsync();
            try
            {
                if (!File.Exists(_filePath)) return new T();
                var json = await File.ReadAllTextAsync(_filePath);
                return JsonSerializer.Deserialize<T>(json, _opts) ?? new T();
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public async Task SaveAsync(T data)
        {
            await _fileLock.WaitAsync();
            try
            {
                await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(data, _opts));
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }
}
