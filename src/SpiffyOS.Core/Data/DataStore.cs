using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace SpiffyOS.Core.Data;

public sealed class DataStore
{
    private readonly string _dbPath;

    public DataStore(string dbPath)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        Init();
    }

    private IDbConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private void Init()
    {
        using var conn = Open();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS quotes (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              text TEXT NOT NULL,
              added_at TEXT NOT NULL,
              added_by_id TEXT,
              added_by_login TEXT
            );
        """);
    }

    // ----- Quotes -----
    public async Task<long> AddQuoteAsync(string text, string? addedById, string? addedByLogin, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Quote text is empty");
        using var conn = Open();
        var now = DateTimeOffset.UtcNow.ToString("o");
        var sql = "INSERT INTO quotes(text,added_at,added_by_id,added_by_login) VALUES(@t,@a,@i,@l); SELECT last_insert_rowid();";
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new { t = text.Trim(), a = now, i = addedById, l = addedByLogin }, cancellationToken: ct));
        return id;
    }

    public async Task<(long id, string text)?> GetRandomQuoteAsync(CancellationToken ct)
    {
        using var conn = Open();
        var row = await conn.QueryFirstOrDefaultAsync<(long id, string text)>(
            new CommandDefinition("SELECT id, text FROM quotes ORDER BY RANDOM() LIMIT 1;", cancellationToken: ct));
        return row == default ? null : row;
    }

    public async Task<(long id, string text)?> GetQuoteByIdAsync(long id, CancellationToken ct)
    {
        using var conn = Open();
        var row = await conn.QueryFirstOrDefaultAsync<(long id, string text)>(
            new CommandDefinition("SELECT id, text FROM quotes WHERE id=@id;", new { id }, cancellationToken: ct));
        return row == default ? null : row;
    }

    public async Task<(long id, string text)?> SearchQuoteAsync(string term, CancellationToken ct)
    {
        using var conn = Open();
        var row = await conn.QueryFirstOrDefaultAsync<(long id, string text)>(
            new CommandDefinition("SELECT id, text FROM quotes WHERE text LIKE @q ORDER BY id ASC LIMIT 1;", new { q = $"%{term}%" }, cancellationToken: ct));
        return row == default ? null : row;
    }

    public async Task<bool> DeleteQuoteAsync(long id, CancellationToken ct)
    {
        using var conn = Open();
        var n = await conn.ExecuteAsync(new CommandDefinition("DELETE FROM quotes WHERE id=@id;", new { id }, cancellationToken: ct));
        return n > 0;
    }
}
