using System.Collections.Concurrent;
using System.Text.Json;

namespace TimeZoneBebek.Repositories
{
    public class JsonRepository<T> where T : new()
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);
        private const int MaxRetryCount = 5;

        private readonly string _filePath;
        private readonly SemaphoreSlim _fileLock;
        private readonly JsonSerializerOptions _opts;

        public JsonRepository(string fileName)
        {
            _filePath = Path.Combine(Directory.GetCurrentDirectory(), "data", fileName);
            _fileLock = FileLocks.GetOrAdd(_filePath, _ => new SemaphoreSlim(1, 1));
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
                if (!File.Exists(_filePath))
                    return new T();

                var json = await RetryOnFileAccessAsync(async () =>
                {
                    using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    return await reader.ReadToEndAsync();
                });

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
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

                await RetryOnFileAccessAsync(async () =>
                {
                    await using var stream = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    await using var writer = new StreamWriter(stream);
                    await writer.WriteAsync(JsonSerializer.Serialize(data, _opts));
                    await writer.FlushAsync();
                });
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private static async Task RetryOnFileAccessAsync(Func<Task> action)
        {
            await RetryOnFileAccessAsync(async () =>
            {
                await action();
                return true;
            });
        }

        private static async Task<TResult> RetryOnFileAccessAsync<TResult>(Func<Task<TResult>> action)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await action();
                }
                catch (IOException) when (attempt < MaxRetryCount)
                {
                    await Task.Delay(attempt * 100);
                }
            }
        }
    }
}
