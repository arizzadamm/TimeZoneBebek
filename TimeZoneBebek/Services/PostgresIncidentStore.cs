using Npgsql;
using NpgsqlTypes;
using TimeZoneBebek.Models;
using TimeZoneBebek.Repositories;

namespace TimeZoneBebek.Services
{
    public class PostgresIncidentStore : IIncidentStore
    {
        private static readonly SemaphoreSlim GlobalSchemaLock = new(1, 1);
        private static readonly HashSet<string> ReadyConnections = new(StringComparer.Ordinal);

        private readonly string _connectionString;
        private readonly JsonRepository<List<Incident>> _jsonFallback = new("incidents.json");

        public PostgresIncidentStore(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async Task<List<Incident>> LoadAsync()
        {
            await EnsureSchemaAsync();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            if (await IsIncidentTableEmptyAsync(connection))
            {
                var seedData = await _jsonFallback.LoadAsync();
                if (seedData.Count > 0)
                    await SaveAsync(seedData);
            }

            const string sql = """
                SELECT
                    i.id,
                    i.date,
                    i.title,
                    i.severity,
                    i.attacker,
                    i.summary,
                    i.tags,
                    i.ai_analysis,
                    i.status,
                    i.owner,
                    i.source,
                    i.affected_asset,
                    i.first_seen,
                    i.last_seen,
                    i.resolution_note,
                    i.updated_at,
                    a.timestamp_utc,
                    a.action,
                    a.actor,
                    a.message
                FROM incidents i
                LEFT JOIN incident_audit_entries a ON a.incident_id = i.id
                ORDER BY i.date DESC, a.sort_order ASC, a.timestamp_utc DESC;
                """;

            var incidents = new Dictionary<string, Incident>(StringComparer.OrdinalIgnoreCase);
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetString(0);
                if (!incidents.TryGetValue(id, out var incident))
                {
                    incident = new Incident
                    {
                        Id = id,
                        Date = reader.GetFieldValue<DateTime>(1),
                        Title = GetRequiredString(reader, 2),
                        Severity = GetRequiredString(reader, 3, "LOW"),
                        Attacker = GetRequiredString(reader, 4),
                        Summary = GetRequiredString(reader, 5),
                        Tags = reader.IsDBNull(6) ? null : reader.GetFieldValue<string[]>(6),
                        AiAnalysis = reader.IsDBNull(7) ? null : reader.GetString(7),
                        Status = GetRequiredString(reader, 8, IncidentStatuses.New),
                        Owner = reader.IsDBNull(9) ? null : reader.GetString(9),
                        Source = reader.IsDBNull(10) ? null : reader.GetString(10),
                        AffectedAsset = reader.IsDBNull(11) ? null : reader.GetString(11),
                        FirstSeen = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTime>(12),
                        LastSeen = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTime>(13),
                        ResolutionNote = reader.IsDBNull(14) ? null : reader.GetString(14),
                        UpdatedAt = reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTime>(15),
                        AuditTrail = []
                    };
                    incidents[id] = incident;
                }

                if (!reader.IsDBNull(16))
                {
                    incident.AuditTrail.Add(new IncidentAuditEntry
                    {
                        TimestampUtc = reader.GetFieldValue<DateTime>(16),
                        Action = GetRequiredString(reader, 17),
                        Actor = GetRequiredString(reader, 18, "SOC_CONSOLE"),
                        Message = GetRequiredString(reader, 19)
                    });
                }
            }

            return incidents.Values.ToList();
        }

        public async Task<Incident?> GetByIdAsync(string id)
        {
            await EnsureSchemaAsync();
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            return (await GetIncidentsByIdsAsync(connection, [id])).FirstOrDefault();
        }

        public async Task AddAsync(Incident incident)
        {
            await EnsureSchemaAsync();
            var prepared = PrepareForPersistence([incident]).Single();
            await UpsertIncidentAsync(prepared, isInsertOnly: true);
        }

        public async Task UpdateAsync(Incident incident)
        {
            await EnsureSchemaAsync();
            var prepared = PrepareForPersistence([incident]).Single();
            await UpsertIncidentAsync(prepared, isInsertOnly: false);
        }

        public async Task<bool> DeleteAsync(string id)
        {
            await EnsureSchemaAsync();
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("DELETE FROM incidents WHERE id = @id;", connection);
            command.Parameters.AddWithValue("id", id);
            return await command.ExecuteNonQueryAsync() > 0;
        }

        public async Task<int> BulkUpdateStatusAsync(IEnumerable<string> ids, string status, Func<Incident, bool> canTransition, Func<Incident, Incident> applyUpdate)
        {
            await EnsureSchemaAsync();
            var idList = ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (idList.Count == 0)
                return 0;

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var incidents = await GetIncidentsByIdsAsync(connection, idList, transaction);
            var updatedCount = 0;
            foreach (var incident in incidents)
            {
                if (!canTransition(incident))
                    continue;

                var updated = applyUpdate(incident);
                await UpsertIncidentAsync(updated, isInsertOnly: false, connection, transaction);
                updatedCount++;
            }

            await transaction.CommitAsync();
            return updatedCount;
        }

        public async Task<IncidentArchivePage> SearchAsync(IncidentArchiveQuery query)
        {
            await EnsureSchemaAsync();
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var page = Math.Max(query.Page, 1);
            var pageSize = Math.Clamp(query.PageSize, 10, 100);
            var offset = (page - 1) * pageSize;
            var search = string.IsNullOrWhiteSpace(query.Search) ? null : $"%{query.Search.Trim().ToLowerInvariant()}%";
            var severity = NormalizeFilter(query.Severity);
            var status = NormalizeFilter(query.Status);

            const string summarySql = """
                SELECT
                    COUNT(*) FILTER (WHERE status NOT IN ('RESOLVED', 'FALSE_POSITIVE')) AS open_count,
                    COUNT(*) FILTER (WHERE severity = 'CRITICAL' AND status NOT IN ('RESOLVED', 'FALSE_POSITIVE')) AS critical_count,
                    COUNT(*) FILTER (WHERE owner IS NOT NULL AND btrim(owner) <> '') AS assigned_count,
                    COUNT(*) FILTER (WHERE status = 'RESOLVED') AS resolved_count,
                    COUNT(*) AS total_count
                FROM incidents;
                """;

            const string countSql = """
                SELECT COUNT(*)
                FROM incidents
                WHERE (NOT @has_search OR lower(concat_ws(' ', title, attacker, id, owner, affected_asset, source)) LIKE @search)
                  AND (NOT @has_severity OR severity = @severity)
                  AND (NOT @has_status OR status = @status);
                """;

            const string pageSql = """
                WITH paged AS (
                    SELECT *
                    FROM incidents
                    WHERE (NOT @has_search OR lower(concat_ws(' ', title, attacker, id, owner, affected_asset, source)) LIKE @search)
                      AND (NOT @has_severity OR severity = @severity)
                      AND (NOT @has_status OR status = @status)
                    ORDER BY date DESC
                    OFFSET @offset LIMIT @page_size
                )
                SELECT
                    p.id,
                    p.date,
                    p.title,
                    p.severity,
                    p.attacker,
                    p.summary,
                    p.tags,
                    p.ai_analysis,
                    p.status,
                    p.owner,
                    p.source,
                    p.affected_asset,
                    p.first_seen,
                    p.last_seen,
                    p.resolution_note,
                    p.updated_at,
                    a.timestamp_utc,
                    a.action,
                    a.actor,
                    a.message
                FROM paged p
                LEFT JOIN incident_audit_entries a ON a.incident_id = p.id
                ORDER BY p.date DESC, a.sort_order ASC, a.timestamp_utc DESC;
                """;

            var summary = new IncidentArchiveSummary();
            await using (var summaryCommand = new NpgsqlCommand(summarySql, connection))
            await using (var reader = await summaryCommand.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    summary.OpenCount = reader.GetInt32(0);
                    summary.CriticalCount = reader.GetInt32(1);
                    summary.AssignedCount = reader.GetInt32(2);
                    summary.ResolvedCount = reader.GetInt32(3);
                    summary.TotalCount = reader.GetInt32(4);
                }
            }

            int filteredCount;
            await using (var countCommand = new NpgsqlCommand(countSql, connection))
            {
                AddArchiveParameters(countCommand, search, severity, status, offset, pageSize);
                filteredCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync() ?? 0);
            }

            var items = new Dictionary<string, Incident>(StringComparer.OrdinalIgnoreCase);
            await using (var pageCommand = new NpgsqlCommand(pageSql, connection))
            {
                AddArchiveParameters(pageCommand, search, severity, status, offset, pageSize);
                await using var reader = await pageCommand.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var id = reader.GetString(0);
                    if (!items.TryGetValue(id, out var incident))
                    {
                        incident = new Incident
                        {
                            Id = id,
                            Date = reader.GetFieldValue<DateTime>(1),
                            Title = GetRequiredString(reader, 2),
                            Severity = GetRequiredString(reader, 3, "LOW"),
                            Attacker = GetRequiredString(reader, 4),
                            Summary = GetRequiredString(reader, 5),
                            Tags = reader.IsDBNull(6) ? null : reader.GetFieldValue<string[]>(6),
                            AiAnalysis = reader.IsDBNull(7) ? null : reader.GetString(7),
                            Status = GetRequiredString(reader, 8, IncidentStatuses.New),
                            Owner = reader.IsDBNull(9) ? null : reader.GetString(9),
                            Source = reader.IsDBNull(10) ? null : reader.GetString(10),
                            AffectedAsset = reader.IsDBNull(11) ? null : reader.GetString(11),
                            FirstSeen = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTime>(12),
                            LastSeen = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTime>(13),
                            ResolutionNote = reader.IsDBNull(14) ? null : reader.GetString(14),
                            UpdatedAt = reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTime>(15),
                            AuditTrail = []
                        };
                        items[id] = incident;
                    }

                    if (!reader.IsDBNull(16))
                    {
                        incident.AuditTrail.Add(new IncidentAuditEntry
                        {
                            TimestampUtc = reader.GetFieldValue<DateTime>(16),
                            Action = GetRequiredString(reader, 17),
                            Actor = GetRequiredString(reader, 18, "SOC_CONSOLE"),
                            Message = GetRequiredString(reader, 19)
                        });
                    }
                }
            }

            summary.FilteredCount = filteredCount;
            return new IncidentArchivePage
            {
                Items = items.Values.ToList(),
                Page = page,
                PageSize = pageSize,
                TotalCount = filteredCount,
                TotalPages = Math.Max((int)Math.Ceiling(filteredCount / (double)pageSize), 1),
                Summary = summary
            };
        }

        public async Task SaveAsync(List<Incident> incidents)
        {
            await EnsureSchemaAsync();
            var normalizedIncidents = PrepareForPersistence(incidents);

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                await using (var deleteAudit = new NpgsqlCommand("DELETE FROM incident_audit_entries;", connection, transaction))
                {
                    await deleteAudit.ExecuteNonQueryAsync();
                }

                await using (var deleteIncidents = new NpgsqlCommand("DELETE FROM incidents;", connection, transaction))
                {
                    await deleteIncidents.ExecuteNonQueryAsync();
                }

                const string insertIncidentSql = """
                    INSERT INTO incidents
                    (
                        id, date, title, severity, attacker, summary, tags, ai_analysis,
                        status, owner, source, affected_asset, first_seen, last_seen,
                        resolution_note, updated_at
                    )
                    VALUES
                    (
                        @id, @date, @title, @severity, @attacker, @summary, @tags, @ai_analysis,
                        @status, @owner, @source, @affected_asset, @first_seen, @last_seen,
                        @resolution_note, @updated_at
                    );
                    """;

                const string insertAuditSql = """
                    INSERT INTO incident_audit_entries
                    (
                        incident_id, timestamp_utc, action, actor, message, sort_order
                    )
                    VALUES
                    (
                        @incident_id, @timestamp_utc, @action, @actor, @message, @sort_order
                    );
                    """;

                foreach (var incident in normalizedIncidents)
                {
                    await using (var insertIncident = new NpgsqlCommand(insertIncidentSql, connection, transaction))
                    {
                        insertIncident.Parameters.AddWithValue("id", incident.Id ?? Guid.NewGuid().ToString("N"));
                        insertIncident.Parameters.AddWithValue("date", incident.Date);
                        insertIncident.Parameters.AddWithValue("title", incident.Title ?? "");
                        insertIncident.Parameters.AddWithValue("severity", incident.Severity ?? "LOW");
                        insertIncident.Parameters.AddWithValue("attacker", incident.Attacker ?? "");
                        insertIncident.Parameters.AddWithValue("summary", incident.Summary ?? "");
                        insertIncident.Parameters.AddWithValue("tags", (object?)incident.Tags ?? DBNull.Value);
                        insertIncident.Parameters.AddWithValue("ai_analysis", (object?)incident.AiAnalysis ?? DBNull.Value);
                        insertIncident.Parameters.AddWithValue("status", incident.Status ?? IncidentStatuses.New);
                        insertIncident.Parameters.AddWithValue("owner", (object?)incident.Owner ?? DBNull.Value);
                        insertIncident.Parameters.AddWithValue("source", (object?)incident.Source ?? DBNull.Value);
                        insertIncident.Parameters.AddWithValue("affected_asset", (object?)incident.AffectedAsset ?? DBNull.Value);
                        insertIncident.Parameters.AddWithValue("first_seen", (object?)incident.FirstSeen ?? DBNull.Value);
                        insertIncident.Parameters.AddWithValue("last_seen", (object?)incident.LastSeen ?? DBNull.Value);
                        insertIncident.Parameters.AddWithValue("resolution_note", (object?)incident.ResolutionNote ?? DBNull.Value);
                        insertIncident.Parameters.AddWithValue("updated_at", (object?)incident.UpdatedAt ?? DBNull.Value);
                        await insertIncident.ExecuteNonQueryAsync();
                    }

                    for (var index = 0; index < (incident.AuditTrail?.Count ?? 0); index++)
                    {
                        var entry = incident.AuditTrail![index];
                        await using var insertAudit = new NpgsqlCommand(insertAuditSql, connection, transaction);
                        insertAudit.Parameters.AddWithValue("incident_id", incident.Id ?? "");
                        insertAudit.Parameters.AddWithValue("timestamp_utc", entry.TimestampUtc);
                        insertAudit.Parameters.AddWithValue("action", entry.Action ?? "");
                        insertAudit.Parameters.AddWithValue("actor", entry.Actor ?? "");
                        insertAudit.Parameters.AddWithValue("message", entry.Message ?? "");
                        insertAudit.Parameters.AddWithValue("sort_order", index);
                        await insertAudit.ExecuteNonQueryAsync();
                    }
                }

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        private async Task EnsureSchemaAsync()
        {
            lock (ReadyConnections)
            {
                if (ReadyConnections.Contains(_connectionString))
                    return;
            }

            await GlobalSchemaLock.WaitAsync();
            try
            {
                lock (ReadyConnections)
                {
                    if (ReadyConnections.Contains(_connectionString))
                        return;
                }

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                const string sql = """
                    CREATE TABLE IF NOT EXISTS incidents
                    (
                        id TEXT PRIMARY KEY,
                        date TIMESTAMPTZ NOT NULL,
                        title VARCHAR(140) NOT NULL,
                        severity VARCHAR(20) NOT NULL,
                        attacker VARCHAR(120) NOT NULL,
                        summary TEXT NOT NULL,
                        tags TEXT[] NULL,
                        ai_analysis TEXT NULL,
                        status VARCHAR(32) NOT NULL,
                        owner TEXT NULL,
                        source TEXT NULL,
                        affected_asset TEXT NULL,
                        first_seen TIMESTAMPTZ NULL,
                        last_seen TIMESTAMPTZ NULL,
                        resolution_note TEXT NULL,
                        updated_at TIMESTAMPTZ NULL
                    );

                    CREATE TABLE IF NOT EXISTS incident_audit_entries
                    (
                        audit_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                        incident_id TEXT NOT NULL REFERENCES incidents(id) ON DELETE CASCADE,
                        timestamp_utc TIMESTAMPTZ NOT NULL,
                        action TEXT NOT NULL,
                        actor TEXT NOT NULL,
                        message TEXT NOT NULL,
                        sort_order INT NOT NULL DEFAULT 0
                    );

                    CREATE INDEX IF NOT EXISTS idx_incidents_date ON incidents(date DESC);
                    CREATE INDEX IF NOT EXISTS idx_incidents_status ON incidents(status);
                    CREATE INDEX IF NOT EXISTS idx_incident_audit_incident_id ON incident_audit_entries(incident_id, sort_order);
                    """;

                await using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync();
                lock (ReadyConnections)
                {
                    ReadyConnections.Add(_connectionString);
                }
            }
            finally
            {
                GlobalSchemaLock.Release();
            }
        }

        private static async Task<bool> IsIncidentTableEmptyAsync(NpgsqlConnection connection)
        {
            await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM incidents;", connection);
            return (long)(await command.ExecuteScalarAsync() ?? 0L) == 0L;
        }

        private static List<Incident> PrepareForPersistence(List<Incident> incidents)
        {
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalized = new List<Incident>(incidents.Count);

            foreach (var incident in incidents)
            {
                var copy = CloneIncident(incident);
                var baseId = string.IsNullOrWhiteSpace(copy.Id) ? GenerateIncidentId() : copy.Id.Trim();
                var uniqueId = baseId;

                while (!seenIds.Add(uniqueId))
                    uniqueId = GenerateIncidentId();

                copy.Id = uniqueId;
                normalized.Add(copy);
            }

            return normalized;
        }

        private static Incident CloneIncident(Incident incident)
        {
            return new Incident
            {
                Id = incident.Id,
                Date = incident.Date,
                Title = incident.Title,
                Severity = incident.Severity,
                Attacker = incident.Attacker,
                Summary = incident.Summary,
                Tags = incident.Tags?.ToArray(),
                AiAnalysis = incident.AiAnalysis,
                Status = incident.Status,
                Owner = incident.Owner,
                Source = incident.Source,
                AffectedAsset = incident.AffectedAsset,
                FirstSeen = incident.FirstSeen,
                LastSeen = incident.LastSeen,
                ResolutionNote = incident.ResolutionNote,
                UpdatedAt = incident.UpdatedAt,
                AuditTrail = incident.AuditTrail?
                    .Select(entry => new IncidentAuditEntry
                    {
                        TimestampUtc = entry.TimestampUtc,
                        Action = entry.Action,
                        Actor = entry.Actor,
                        Message = entry.Message
                    })
                    .ToList() ?? []
            };
        }

        private static string GenerateIncidentId() =>
            "INC-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        private async Task UpsertIncidentAsync(Incident incident, bool isInsertOnly)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                await UpsertIncidentAsync(incident, isInsertOnly, connection, transaction);

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        private async Task UpsertIncidentAsync(Incident incident, bool isInsertOnly, NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            var sql = isInsertOnly
                ? """
                    INSERT INTO incidents
                    (
                        id, date, title, severity, attacker, summary, tags, ai_analysis,
                        status, owner, source, affected_asset, first_seen, last_seen,
                        resolution_note, updated_at
                    )
                    VALUES
                    (
                        @id, @date, @title, @severity, @attacker, @summary, @tags, @ai_analysis,
                        @status, @owner, @source, @affected_asset, @first_seen, @last_seen,
                        @resolution_note, @updated_at
                    );
                    """
                : """
                    UPDATE incidents
                    SET
                        date = @date,
                        title = @title,
                        severity = @severity,
                        attacker = @attacker,
                        summary = @summary,
                        tags = @tags,
                        ai_analysis = @ai_analysis,
                        status = @status,
                        owner = @owner,
                        source = @source,
                        affected_asset = @affected_asset,
                        first_seen = @first_seen,
                        last_seen = @last_seen,
                        resolution_note = @resolution_note,
                        updated_at = @updated_at
                    WHERE id = @id;
                    """;

            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", incident.Id ?? GenerateIncidentId());
                command.Parameters.AddWithValue("date", incident.Date);
                command.Parameters.AddWithValue("title", incident.Title ?? "");
                command.Parameters.AddWithValue("severity", incident.Severity ?? "LOW");
                command.Parameters.AddWithValue("attacker", incident.Attacker ?? "");
                command.Parameters.AddWithValue("summary", incident.Summary ?? "");
                command.Parameters.AddWithValue("tags", (object?)incident.Tags ?? DBNull.Value);
                command.Parameters.AddWithValue("ai_analysis", (object?)incident.AiAnalysis ?? DBNull.Value);
                command.Parameters.AddWithValue("status", incident.Status ?? IncidentStatuses.New);
                command.Parameters.AddWithValue("owner", (object?)incident.Owner ?? DBNull.Value);
                command.Parameters.AddWithValue("source", (object?)incident.Source ?? DBNull.Value);
                command.Parameters.AddWithValue("affected_asset", (object?)incident.AffectedAsset ?? DBNull.Value);
                command.Parameters.AddWithValue("first_seen", (object?)incident.FirstSeen ?? DBNull.Value);
                command.Parameters.AddWithValue("last_seen", (object?)incident.LastSeen ?? DBNull.Value);
                command.Parameters.AddWithValue("resolution_note", (object?)incident.ResolutionNote ?? DBNull.Value);
                command.Parameters.AddWithValue("updated_at", (object?)incident.UpdatedAt ?? DBNull.Value);
                await command.ExecuteNonQueryAsync();
            }

            await using (var deleteAudit = new NpgsqlCommand("DELETE FROM incident_audit_entries WHERE incident_id = @id;", connection, transaction))
            {
                deleteAudit.Parameters.AddWithValue("id", incident.Id ?? "");
                await deleteAudit.ExecuteNonQueryAsync();
            }

            const string insertAuditSql = """
                INSERT INTO incident_audit_entries
                (
                    incident_id, timestamp_utc, action, actor, message, sort_order
                )
                VALUES
                (
                    @incident_id, @timestamp_utc, @action, @actor, @message, @sort_order
                );
                """;

            for (var index = 0; index < (incident.AuditTrail?.Count ?? 0); index++)
            {
                var entry = incident.AuditTrail![index];
                await using var insertAudit = new NpgsqlCommand(insertAuditSql, connection, transaction);
                insertAudit.Parameters.AddWithValue("incident_id", incident.Id ?? "");
                insertAudit.Parameters.AddWithValue("timestamp_utc", entry.TimestampUtc);
                insertAudit.Parameters.AddWithValue("action", entry.Action ?? "");
                insertAudit.Parameters.AddWithValue("actor", entry.Actor ?? "");
                insertAudit.Parameters.AddWithValue("message", entry.Message ?? "");
                insertAudit.Parameters.AddWithValue("sort_order", index);
                await insertAudit.ExecuteNonQueryAsync();
            }
        }

        private static async Task<List<Incident>> GetIncidentsByIdsAsync(NpgsqlConnection connection, IReadOnlyCollection<string> ids, NpgsqlTransaction? transaction = null)
        {
            if (ids.Count == 0)
                return [];

            const string sql = """
                SELECT
                    i.id,
                    i.date,
                    i.title,
                    i.severity,
                    i.attacker,
                    i.summary,
                    i.tags,
                    i.ai_analysis,
                    i.status,
                    i.owner,
                    i.source,
                    i.affected_asset,
                    i.first_seen,
                    i.last_seen,
                    i.resolution_note,
                    i.updated_at,
                    a.timestamp_utc,
                    a.action,
                    a.actor,
                    a.message
                FROM incidents i
                LEFT JOIN incident_audit_entries a ON a.incident_id = i.id
                WHERE i.id = ANY(@ids)
                ORDER BY i.date DESC, a.sort_order ASC, a.timestamp_utc DESC;
                """;

            var incidents = new Dictionary<string, Incident>(StringComparer.OrdinalIgnoreCase);
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("ids", ids.ToArray());
            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var id = reader.GetString(0);
                if (!incidents.TryGetValue(id, out var incident))
                {
                    incident = new Incident
                    {
                        Id = id,
                        Date = reader.GetFieldValue<DateTime>(1),
                        Title = GetRequiredString(reader, 2),
                        Severity = GetRequiredString(reader, 3, "LOW"),
                        Attacker = GetRequiredString(reader, 4),
                        Summary = GetRequiredString(reader, 5),
                        Tags = reader.IsDBNull(6) ? null : reader.GetFieldValue<string[]>(6),
                        AiAnalysis = reader.IsDBNull(7) ? null : reader.GetString(7),
                        Status = GetRequiredString(reader, 8, IncidentStatuses.New),
                        Owner = reader.IsDBNull(9) ? null : reader.GetString(9),
                        Source = reader.IsDBNull(10) ? null : reader.GetString(10),
                        AffectedAsset = reader.IsDBNull(11) ? null : reader.GetString(11),
                        FirstSeen = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTime>(12),
                        LastSeen = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTime>(13),
                        ResolutionNote = reader.IsDBNull(14) ? null : reader.GetString(14),
                        UpdatedAt = reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTime>(15),
                        AuditTrail = []
                    };
                    incidents[id] = incident;
                }

                if (!reader.IsDBNull(16))
                {
                    incident.AuditTrail.Add(new IncidentAuditEntry
                    {
                        TimestampUtc = reader.GetFieldValue<DateTime>(16),
                        Action = GetRequiredString(reader, 17),
                        Actor = GetRequiredString(reader, 18, "SOC_CONSOLE"),
                        Message = GetRequiredString(reader, 19)
                    });
                }
            }

            return incidents.Values.ToList();
        }

        private static void AddArchiveParameters(NpgsqlCommand command, string? search, string? severity, string? status, int offset, int pageSize)
        {
            var hasSearch = !string.IsNullOrWhiteSpace(search);
            var hasSeverity = !string.IsNullOrWhiteSpace(severity);
            var hasStatus = !string.IsNullOrWhiteSpace(status);

            command.Parameters.Add(new NpgsqlParameter("search", NpgsqlDbType.Text)
            {
                Value = hasSearch ? search! : string.Empty
            });
            command.Parameters.Add(new NpgsqlParameter("severity", NpgsqlDbType.Text)
            {
                Value = hasSeverity ? severity! : string.Empty
            });
            command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text)
            {
                Value = hasStatus ? status! : string.Empty
            });
            command.Parameters.Add(new NpgsqlParameter("has_search", NpgsqlDbType.Boolean)
            {
                Value = hasSearch
            });
            command.Parameters.Add(new NpgsqlParameter("has_severity", NpgsqlDbType.Boolean)
            {
                Value = hasSeverity
            });
            command.Parameters.Add(new NpgsqlParameter("has_status", NpgsqlDbType.Boolean)
            {
                Value = hasStatus
            });
            command.Parameters.Add(new NpgsqlParameter("offset", NpgsqlDbType.Integer)
            {
                Value = offset
            });
            command.Parameters.Add(new NpgsqlParameter("page_size", NpgsqlDbType.Integer)
            {
                Value = pageSize
            });
        }

        private static string? NormalizeFilter(string? value)
        {
            var normalized = (value ?? "ALL").Trim().ToUpperInvariant();
            return normalized == "ALL" || normalized == "" ? null : normalized;
        }

        private static string GetRequiredString(NpgsqlDataReader reader, int ordinal, string fallback = "")
        {
            if (reader.IsDBNull(ordinal))
                return fallback;

            return reader.GetString(ordinal);
        }
    }
}
