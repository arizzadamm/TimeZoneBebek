using TimeZoneBebek.Models;

namespace TimeZoneBebek.Services
{
    public class MonitoringState
    {
        private readonly object _lock = new();
        private DashboardHealth _health = new();

        public DashboardHealth GetHealth()
        {
            lock (_lock)
            {
                return new DashboardHealth
                {
                    ElasticHealthy = _health.ElasticHealthy,
                    EpsHealthy = _health.EpsHealthy,
                    ThreatWebhookHealthy = _health.ThreatWebhookHealthy,
                    FeedHealthy = _health.FeedHealthy,
                    LastElasticSuccessUtc = _health.LastElasticSuccessUtc,
                    LastBroadcastUtc = _health.LastBroadcastUtc,
                    LastWebhookSuccessUtc = _health.LastWebhookSuccessUtc,
                    LastEpsSuccessUtc = _health.LastEpsSuccessUtc,
                    LastElasticError = _health.LastElasticError,
                    LastWebhookError = _health.LastWebhookError,
                    LastKnownEventsPerSecond = _health.LastKnownEventsPerSecond,
                    LastKnownEventsPerMinute = _health.LastKnownEventsPerMinute
                };
            }
        }

        public void MarkElasticSuccess()
        {
            lock (_lock)
            {
                _health.ElasticHealthy = true;
                _health.FeedHealthy = true;
                _health.LastElasticSuccessUtc = DateTime.UtcNow;
                _health.LastElasticError = null;
            }
        }

        public void MarkElasticFailure(string error)
        {
            lock (_lock)
            {
                _health.ElasticHealthy = false;
                _health.FeedHealthy = false;
                _health.LastElasticError = error;
            }
        }

        public void MarkBroadcast()
        {
            lock (_lock)
            {
                _health.LastBroadcastUtc = DateTime.UtcNow;
                _health.FeedHealthy = true;
            }
        }

        public void MarkWebhookSuccess()
        {
            lock (_lock)
            {
                _health.ThreatWebhookHealthy = true;
                _health.LastWebhookSuccessUtc = DateTime.UtcNow;
                _health.LastWebhookError = null;
            }
        }

        public void MarkWebhookFailure(string error)
        {
            lock (_lock)
            {
                _health.ThreatWebhookHealthy = false;
                _health.LastWebhookError = error;
            }
        }

        public void MarkEpsSuccess(EpsSnapshot snapshot)
        {
            lock (_lock)
            {
                _health.EpsHealthy = true;
                _health.LastEpsSuccessUtc = snapshot.CapturedAtUtc;
                _health.LastKnownEventsPerSecond = snapshot.EventsPerSecond;
                _health.LastKnownEventsPerMinute = snapshot.EventsLastMinute;
            }
        }

        public void MarkEpsFailure()
        {
            lock (_lock)
            {
                _health.EpsHealthy = false;
            }
        }
    }
}
