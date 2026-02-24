using System.Text.Json;
using Magent.Core;
using Microsoft.Data.Sqlite;

namespace Magent.Data;

public sealed class SqliteStore(string dbPath)
{
    public string DbPath { get; } = dbPath;

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        var sql = """
CREATE TABLE IF NOT EXISTS character_orders (
  order_id INTEGER PRIMARY KEY,
  type_id INTEGER NOT NULL,
  is_buy_order INTEGER NOT NULL,
  price REAL NOT NULL,
  volume_remain INTEGER NOT NULL,
  location_id INTEGER NOT NULL,
  issued_at TEXT NOT NULL,
  expires_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS orderbook_snapshots (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  type_id INTEGER NOT NULL,
  captured_at TEXT NOT NULL,
  payload TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS opportunities (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  fingerprint TEXT NOT NULL,
  kind TEXT NOT NULL,
  type_id INTEGER NOT NULL,
  net_margin_pct REAL NOT NULL,
  estimated_profit_isk REAL NOT NULL,
  confidence TEXT NOT NULL,
  notes TEXT NOT NULL,
  detected_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS alerts_sent (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  fingerprint TEXT NOT NULL UNIQUE,
  sent_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS watchlist (
  type_id INTEGER PRIMARY KEY,
  last_seen_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS recommendation_history (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  fingerprint TEXT NOT NULL UNIQUE,
  type_id INTEGER NOT NULL,
  kind TEXT NOT NULL,
  first_seen_at TEXT NOT NULL,
  last_seen_at TEXT NOT NULL,
  initial_margin_pct REAL NOT NULL,
  latest_margin_pct REAL NOT NULL,
  status TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS recommendation_outcomes (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  history_id INTEGER NOT NULL,
  outcome TEXT NOT NULL,
  recorded_at TEXT NOT NULL,
  FOREIGN KEY(history_id) REFERENCES recommendation_history(id)
);
""";

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void ReplaceCharacterOrders(IEnumerable<CharacterOrder> orders)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        Execute(connection, "DELETE FROM character_orders;");

        foreach (var o in orders)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO character_orders(order_id,type_id,is_buy_order,price,volume_remain,location_id,issued_at,expires_at) VALUES ($id,$type,$buy,$price,$vol,$loc,$issued,$expires);";
            cmd.Parameters.AddWithValue("$id", o.OrderId);
            cmd.Parameters.AddWithValue("$type", o.TypeId);
            cmd.Parameters.AddWithValue("$buy", o.IsBuyOrder ? 1 : 0);
            cmd.Parameters.AddWithValue("$price", o.Price);
            cmd.Parameters.AddWithValue("$vol", o.VolumeRemain);
            cmd.Parameters.AddWithValue("$loc", o.LocationId);
            cmd.Parameters.AddWithValue("$issued", o.IssuedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$expires", o.ExpiresAt.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<CharacterOrder> GetCharacterOrders()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT order_id,type_id,is_buy_order,price,volume_remain,location_id,issued_at,expires_at FROM character_orders;";
        using var reader = cmd.ExecuteReader();
        var rows = new List<CharacterOrder>();
        while (reader.Read())
        {
            rows.Add(new CharacterOrder(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt64(2) == 1,
                reader.GetDecimal(3),
                reader.GetInt32(4),
                reader.GetInt64(5),
                DateTimeOffset.Parse(reader.GetString(6)),
                DateTimeOffset.Parse(reader.GetString(7))));
        }

        return rows;
    }

    public void SaveWatchlist(IEnumerable<int> typeIds)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        foreach (var typeId in typeIds.Distinct())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO watchlist(type_id,last_seen_at) VALUES($type,$seen) ON CONFLICT(type_id) DO UPDATE SET last_seen_at=excluded.last_seen_at;";
            cmd.Parameters.AddWithValue("$type", typeId);
            cmd.Parameters.AddWithValue("$seen", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<int> GetWatchlist(int maxSize)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT type_id FROM watchlist ORDER BY last_seen_at DESC LIMIT $max;";
        cmd.Parameters.AddWithValue("$max", maxSize);
        using var reader = cmd.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    public IReadOnlyList<int> GetWatchlist()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT type_id FROM watchlist ORDER BY last_seen_at DESC;";
        using var reader = cmd.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    public void AddWatchItem(int typeId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO watchlist(type_id,last_seen_at) VALUES($type,$seen) ON CONFLICT(type_id) DO UPDATE SET last_seen_at=excluded.last_seen_at;";
        cmd.Parameters.AddWithValue("$type", typeId);
        cmd.Parameters.AddWithValue("$seen", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public bool RemoveWatchItem(int typeId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM watchlist WHERE type_id=$type;";
        cmd.Parameters.AddWithValue("$type", typeId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void SaveOrderbookSnapshot(int typeId, IReadOnlyList<MarketOrder> orders)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO orderbook_snapshots(type_id,captured_at,payload) VALUES($type,$captured,$payload);";
        cmd.Parameters.AddWithValue("$type", typeId);
        cmd.Parameters.AddWithValue("$captured", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(orders));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<MarketOrder> GetLatestMarketOrders()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT payload FROM orderbook_snapshots ORDER BY id DESC LIMIT 200;";
        using var reader = cmd.ExecuteReader();
        var all = new List<MarketOrder>();
        while (reader.Read())
        {
            var payload = reader.GetString(0);
            var batch = JsonSerializer.Deserialize<List<MarketOrder>>(payload) ?? [];
            all.AddRange(batch);
        }

        return all;
    }

    public void SaveOpportunities(IEnumerable<Opportunity> opportunities)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        foreach (var item in opportunities)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO opportunities(fingerprint,kind,type_id,net_margin_pct,estimated_profit_isk,confidence,notes,detected_at) VALUES ($f,$k,$t,$n,$p,$c,$notes,$detected);";
            cmd.Parameters.AddWithValue("$f", item.Fingerprint);
            cmd.Parameters.AddWithValue("$k", item.Kind.ToString());
            cmd.Parameters.AddWithValue("$t", item.TypeId);
            cmd.Parameters.AddWithValue("$n", item.NetMarginPct);
            cmd.Parameters.AddWithValue("$p", item.EstimatedProfitIsk);
            cmd.Parameters.AddWithValue("$c", item.Confidence.ToString());
            cmd.Parameters.AddWithValue("$notes", item.Notes);
            cmd.Parameters.AddWithValue("$detected", item.DetectedAt.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<Opportunity> GetLatestOpportunities(int maxCount)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT kind,type_id,net_margin_pct,estimated_profit_isk,confidence,notes,detected_at,fingerprint FROM opportunities ORDER BY id DESC LIMIT $max;";
        cmd.Parameters.AddWithValue("$max", maxCount);
        using var reader = cmd.ExecuteReader();
        var items = new List<Opportunity>();
        while (reader.Read())
        {
            var kind = Enum.Parse<OpportunityKind>(reader.GetString(0), ignoreCase: true);
            var typeId = reader.GetInt32(1);
            var netMargin = reader.GetDecimal(2);
            var estimatedProfit = reader.GetDecimal(3);
            var confidence = Enum.Parse<ConfidenceLevel>(reader.GetString(4), ignoreCase: true);
            var notes = reader.GetString(5);
            var detectedAt = DateTimeOffset.Parse(reader.GetString(6));
            var fingerprint = reader.GetString(7);
            items.Add(new Opportunity(kind, typeId, $"{kind} type {typeId}", notes, netMargin, estimatedProfit, 0m, 0m, 0L, 0, 0m, confidence, detectedAt, fingerprint));
        }

        return items;
    }

    public bool TryMarkAlertSent(string fingerprint)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO alerts_sent(fingerprint,sent_at) VALUES($f,$sent) ON CONFLICT(fingerprint) DO NOTHING; SELECT changes();";
        cmd.Parameters.AddWithValue("$f", fingerprint);
        cmd.Parameters.AddWithValue("$sent", DateTimeOffset.UtcNow.ToString("O"));
        var changed = Convert.ToInt32(cmd.ExecuteScalar());
        return changed > 0;
    }


    public void TrackRecommendationHistory(IEnumerable<Opportunity> opportunities, DateTimeOffset nowUtc)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();

        foreach (var opportunity in opportunities)
        {
            using var upsert = connection.CreateCommand();
            upsert.CommandText = """
INSERT INTO recommendation_history(fingerprint,type_id,kind,first_seen_at,last_seen_at,initial_margin_pct,latest_margin_pct,status)
VALUES($f,$type,$kind,$first,$last,$initial,$latest,'active')
ON CONFLICT(fingerprint) DO UPDATE SET
  last_seen_at=excluded.last_seen_at,
  latest_margin_pct=excluded.latest_margin_pct,
  status='active';
""";
            upsert.Parameters.AddWithValue("$f", opportunity.Fingerprint);
            upsert.Parameters.AddWithValue("$type", opportunity.TypeId);
            upsert.Parameters.AddWithValue("$kind", opportunity.Kind.ToString());
            upsert.Parameters.AddWithValue("$first", nowUtc.ToString("O"));
            upsert.Parameters.AddWithValue("$last", nowUtc.ToString("O"));
            upsert.Parameters.AddWithValue("$initial", opportunity.NetMarginPct);
            upsert.Parameters.AddWithValue("$latest", opportunity.NetMarginPct);
            upsert.ExecuteNonQuery();
        }

        using var expire = connection.CreateCommand();
        expire.CommandText = "UPDATE recommendation_history SET status='expired' WHERE status='active' AND last_seen_at < $cutoff;";
        expire.Parameters.AddWithValue("$cutoff", nowUtc.AddHours(-24).ToString("O"));
        expire.ExecuteNonQuery();

        using var outcomes = connection.CreateCommand();
        outcomes.CommandText = """
INSERT INTO recommendation_outcomes(history_id,outcome,recorded_at)
SELECT h.id,
       CASE WHEN h.latest_margin_pct >= h.initial_margin_pct THEN 'improved' ELSE 'regressed' END,
       $recorded
FROM recommendation_history h
WHERE h.status='active'
AND NOT EXISTS (
  SELECT 1 FROM recommendation_outcomes o
  WHERE o.history_id = h.id
  AND o.recorded_at >= $cycleStart
);
""";
        outcomes.Parameters.AddWithValue("$recorded", nowUtc.ToString("O"));
        outcomes.Parameters.AddWithValue("$cycleStart", nowUtc.AddMinutes(-30).ToString("O"));
        outcomes.ExecuteNonQuery();

        tx.Commit();
    }

    public PerformanceSnapshot GetPerformanceSnapshot()
    {
        using var connection = Open();

        var total = ScalarInt(connection, "SELECT COUNT(*) FROM recommendation_history;");
        var active = ScalarInt(connection, "SELECT COUNT(*) FROM recommendation_history WHERE status='active';");
        var expired = ScalarInt(connection, "SELECT COUNT(*) FROM recommendation_history WHERE status='expired';");
        var improved = ScalarInt(connection, "SELECT COUNT(*) FROM recommendation_outcomes WHERE outcome='improved';");
        var rate = total == 0 ? 0m : Math.Round((decimal)improved / total * 100m, 2);

        return new PerformanceSnapshot(total, active, improved, expired, rate);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        return connection;
    }

    private static int ScalarInt(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
