using TimeZoneBebek.Models;

namespace TimeZoneBebek.Services
{
    public class NmsState
    {
        private readonly object _lock = new();
        private NmsStatusSnapshot? _latest;
        private readonly List<NmsHistoryPoint> _history = [];
        private const int MaxHistoryPoints = 7000;

        public NmsStatusSnapshot? GetLatest()
        {
            lock (_lock)
            {
                return _latest;
            }
        }

        public void SetLatest(NmsStatusSnapshot snapshot)
        {
            lock (_lock)
            {
                _latest = snapshot;
                _history.Add(ToHistoryPoint(snapshot));
                if (_history.Count > MaxHistoryPoints)
                {
                    _history.RemoveRange(0, _history.Count - MaxHistoryPoints);
                }
            }
        }

        public IReadOnlyList<NmsHistoryPoint> GetHistory(TimeSpan range)
        {
            lock (_lock)
            {
                var threshold = DateTime.UtcNow.Subtract(range);
                return _history
                    .Where(point => point.GeneratedAtUtc >= threshold)
                    .Select(CloneHistoryPoint)
                    .ToList();
            }
        }

        private static NmsHistoryPoint ToHistoryPoint(NmsStatusSnapshot snapshot)
        {
            var latencies = snapshot.Categories
                .SelectMany(category => category.Items)
                .Where(item => item.LatencyMs.HasValue)
                .Select(item => item.LatencyMs!.Value)
                .ToList();

            return new NmsHistoryPoint
            {
                GeneratedAtUtc = snapshot.GeneratedAtUtc,
                UpCount = snapshot.Summary.UpCount,
                DownCount = snapshot.Summary.DownCount,
                DegradedCount = snapshot.Summary.DegradedCount,
                UnknownCount = snapshot.Summary.UnknownCount,
                AverageLatencyMs = latencies.Count > 0 ? Convert.ToInt64(Math.Round(latencies.Average())) : null,
                Categories = snapshot.Categories.Select(category => new NmsHistoryCategoryPoint
                {
                    Name = category.Name,
                    UpCount = category.Items.Count(item => item.Status == NmsStatuses.Up),
                    DownCount = category.Items.Count(item => item.Status == NmsStatuses.Down),
                    DegradedCount = category.Items.Count(item => item.Status == NmsStatuses.Degraded),
                    UnknownCount = category.Items.Count(item => item.Status == NmsStatuses.Unknown)
                }).ToList(),
                DownTargets = snapshot.Categories
                    .SelectMany(category => category.Items)
                    .SelectMany(item => (item.Targets ?? [])
                        .Where(target => target.Status == NmsStatuses.Down)
                        .Select(target => new NmsHistoryDownTarget
                        {
                            ItemName = item.Name,
                            TargetName = target.Name,
                            TargetAddress = target.Target,
                            Detail = target.Detail,
                            SeenAtUtc = snapshot.GeneratedAtUtc
                        }))
                    .ToList()
            };
        }

        private static NmsHistoryPoint CloneHistoryPoint(NmsHistoryPoint point)
        {
            return new NmsHistoryPoint
            {
                GeneratedAtUtc = point.GeneratedAtUtc,
                UpCount = point.UpCount,
                DownCount = point.DownCount,
                DegradedCount = point.DegradedCount,
                UnknownCount = point.UnknownCount,
                AverageLatencyMs = point.AverageLatencyMs,
                Categories = point.Categories.Select(category => new NmsHistoryCategoryPoint
                {
                    Name = category.Name,
                    UpCount = category.UpCount,
                    DownCount = category.DownCount,
                    DegradedCount = category.DegradedCount,
                    UnknownCount = category.UnknownCount
                }).ToList(),
                DownTargets = point.DownTargets.Select(target => new NmsHistoryDownTarget
                {
                    ItemName = target.ItemName,
                    TargetName = target.TargetName,
                    TargetAddress = target.TargetAddress,
                    Detail = target.Detail,
                    SeenAtUtc = target.SeenAtUtc
                }).ToList()
            };
        }
    }
}
