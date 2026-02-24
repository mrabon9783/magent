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

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
