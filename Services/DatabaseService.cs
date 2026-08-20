using System.IO;
using Microsoft.Data.Sqlite;
using FinanceAPP.Models;

namespace FinanceAPP.Services;

/// <summary>
/// SQLite本地数据库服务
/// 存储股票K线数据，支持增量更新和离线访问
/// </summary>
public class DatabaseService : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    public DatabaseService(string? dbPath = null)
    {
        dbPath ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "finance.db");
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        Initialize();
    }

    private void Initialize()
    {
        ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS candle_data (
                symbol    TEXT NOT NULL,
                date      TEXT NOT NULL,
                open      REAL NOT NULL,
                high      REAL NOT NULL,
                low       REAL NOT NULL,
                close     REAL NOT NULL,
                volume    REAL NOT NULL,
                amount    REAL NOT NULL DEFAULT 0,
                amplitude REAL NOT NULL DEFAULT 0,
                PRIMARY KEY (symbol, date)
            );
            """);

        // 迁移：为旧数据库添加新列（如果不存在）
        EnsureColumn("candle_data", "amount", "REAL NOT NULL DEFAULT 0");
        EnsureColumn("candle_data", "amplitude", "REAL NOT NULL DEFAULT 0");

        ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS symbol_info (
                symbol   TEXT PRIMARY KEY,
                name     TEXT,
                exchange TEXT,
                currency TEXT
            );
            """);

        ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS query_log (
                symbol       TEXT NOT NULL,
                last_updated TEXT NOT NULL,
                PRIMARY KEY (symbol)
            );
            """);
    }

    /// <summary>
    /// 保存K线数据（INSERT OR REPLACE，自动去重）
    /// </summary>
    public void SaveCandles(string symbol, List<CandleStickData> candles)
    {
        if (candles.Count == 0) return;

        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();

            foreach (var c in candles)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO candle_data (symbol, date, open, high, low, close, volume, amount, amplitude)
                    VALUES (@symbol, @date, @open, @high, @low, @close, @volume, @amount, @amplitude)
                    """;
                cmd.Parameters.AddWithValue("@symbol", symbol);
                cmd.Parameters.AddWithValue("@date", c.Date.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("@open", c.Open);
                cmd.Parameters.AddWithValue("@high", c.High);
                cmd.Parameters.AddWithValue("@low", c.Low);
                cmd.Parameters.AddWithValue("@close", c.Close);
                cmd.Parameters.AddWithValue("@volume", c.Volume);
                cmd.Parameters.AddWithValue("@amount", c.Amount);
                cmd.Parameters.AddWithValue("@amplitude", c.Amplitude);
                cmd.ExecuteNonQuery();
            }

            // 更新查询日志
            using var logCmd = _connection.CreateCommand();
            logCmd.Transaction = transaction;
            logCmd.CommandText = """
                INSERT OR REPLACE INTO query_log (symbol, last_updated)
                VALUES (@symbol, @now)
                """;
            logCmd.Parameters.AddWithValue("@symbol", symbol);
            logCmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            logCmd.ExecuteNonQuery();

            transaction.Commit();
        }
    }

    /// <summary>
    /// 保存股票元信息（名称、交易所、货币）
    /// </summary>
    public void SaveSymbolInfo(string symbol, string name, string exchange, string currency)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO symbol_info (symbol, name, exchange, currency)
                VALUES (@symbol, @name, @exchange, @currency)
                """;
            cmd.Parameters.AddWithValue("@symbol", symbol);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@exchange", exchange);
            cmd.Parameters.AddWithValue("@currency", currency);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 获取股票元信息
    /// </summary>
    public (string name, string exchange, string currency)? GetSymbolInfo(string symbol)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT name, exchange, currency FROM symbol_info WHERE symbol = @symbol";
            cmd.Parameters.AddWithValue("@symbol", symbol);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
            }
            return null;
        }
    }

    /// <summary>
    /// 获取数据库中某股票的最新日期
    /// </summary>
    public DateTime? GetLatestDate(string symbol)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT MAX(date) FROM candle_data WHERE symbol = @symbol";
            cmd.Parameters.AddWithValue("@symbol", symbol);
            var result = cmd.ExecuteScalar();
            if (result == null || result == DBNull.Value) return null;
            return DateTime.Parse((string)result);
        }
    }

    /// <summary>
    /// 获取数据库中某股票的最早日期
    /// </summary>
    public DateTime? GetEarliestDate(string symbol)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT MIN(date) FROM candle_data WHERE symbol = @symbol";
            cmd.Parameters.AddWithValue("@symbol", symbol);
            var result = cmd.ExecuteScalar();
            if (result == null || result == DBNull.Value) return null;
            return DateTime.Parse((string)result);
        }
    }

    /// <summary>
    /// 获取最近N条K线数据
    /// </summary>
    public List<CandleStickData> GetCandles(string symbol, int count)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT date, open, high, low, close, volume, amount, amplitude
                FROM candle_data
                WHERE symbol = @symbol
                ORDER BY date DESC
                LIMIT @count
                """;
            cmd.Parameters.AddWithValue("@symbol", symbol);
            cmd.Parameters.AddWithValue("@count", count);

            var candles = new List<CandleStickData>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                candles.Add(new CandleStickData
                {
                    Date = DateTime.Parse(reader.GetString(0)),
                    Open = reader.GetDouble(1),
                    High = reader.GetDouble(2),
                    Low = reader.GetDouble(3),
                    Close = reader.GetDouble(4),
                    Volume = reader.GetDouble(5),
                    Amount = reader.GetDouble(6),
                    Amplitude = reader.GetDouble(7)
                });
            }

            candles.Reverse(); // 升序排列
            return candles;
        }
    }

    /// <summary>
    /// 判断数据库中的数据是否足够新鲜（可免API调用）
    /// 规则：最新日期 >= 最近的交易日
    /// </summary>
    public bool IsDataFresh(string symbol)
    {
        var latestDate = GetLatestDate(symbol);
        if (!latestDate.HasValue) return false;

        var today = DateTime.Today;
        var latest = latestDate.Value.Date;

        // 数据日期是今天 -> 新鲜
        if (latest >= today) return true;

        // 周末：最近交易日（周五）的数据算新鲜
        if (today.DayOfWeek == DayOfWeek.Saturday || today.DayOfWeek == DayOfWeek.Sunday)
        {
            var expected = today;
            while (expected.DayOfWeek == DayOfWeek.Saturday || expected.DayOfWeek == DayOfWeek.Sunday)
                expected = expected.AddDays(-1);
            return latest >= expected;
        }

        // 交易日：必须有今天的数据才算新鲜
        return false;
    }

    /// <summary>
    /// 获取数据库中某股票的K线总数
    /// </summary>
    public int GetCandleCount(string symbol)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM candle_data WHERE symbol = @symbol";
            cmd.Parameters.AddWithValue("@symbol", symbol);
            return (int)(long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// 获取某股票的数据条数和日期范围
    /// </summary>
    public (int count, DateTime? earliest, DateTime? latest)? GetDataRange(string symbol)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), MIN(date), MAX(date) FROM candle_data WHERE symbol = @symbol";
            cmd.Parameters.AddWithValue("@symbol", symbol);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                int count = (int)(long)reader[0];
                if (count == 0) return null;
                DateTime? earliest = reader[1] == DBNull.Value ? null : DateTime.Parse((string)reader[1]);
                DateTime? latest = reader[2] == DBNull.Value ? null : DateTime.Parse((string)reader[2]);
                return (count, earliest, latest);
            }
            return null;
        }
    }

    /// <summary>
    /// 获取数据库中所有股票代码（从candle_data表）
    /// </summary>
    public List<string> GetAllSymbols()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT symbol FROM candle_data ORDER BY symbol";
            using var reader = cmd.ExecuteReader();
            var list = new List<string>();
            while (reader.Read())
            {
                list.Add((string)reader[0]);
            }
            return list;
        }
    }

    /// <summary>
    /// 获取数据库中所有股票代码和名称（从candle_data + symbol_info表）
    /// </summary>
    public List<(string symbol, string name)> GetAllSymbolsWithName()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT c.symbol, COALESCE(s.name, '') as name
                FROM (SELECT DISTINCT symbol FROM candle_data) c
                LEFT JOIN symbol_info s ON c.symbol = s.symbol
                ORDER BY c.symbol";
            using var reader = cmd.ExecuteReader();
            var list = new List<(string symbol, string name)>();
            while (reader.Read())
            {
                list.Add(((string)reader[0], (string)reader[1]));
            }
            return list;
        }
    }

    private void ExecuteNonQuery(string sql)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 检查并添加缺失的列（数据库迁移）
    /// </summary>
    private void EnsureColumn(string table, string column, string definition)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var reader = cmd.ExecuteReader();
            var columns = new HashSet<string>();
            while (reader.Read())
                columns.Add(reader.GetString(1));

            if (!columns.Contains(column))
            {
                reader.Close();
                ExecuteNonQuery($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
            }
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}