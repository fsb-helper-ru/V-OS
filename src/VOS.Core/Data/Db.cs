using Microsoft.Data.Sqlite;

namespace VOS.Core.Data;

/// <summary>Small SQLite helper: one database file per module, WAL journaling, schema versioning via user_version.</summary>
public sealed class Db
{
    private readonly string _connectionString;
    public string Path { get; }

    public Db(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
    }

    public static Db ForModule(string name) => new(VosPaths.Db(name));

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return c;
    }

    /// <summary>Applies migrations whose index is above the stored user_version, each in its own transaction.</summary>
    public void Migrate(params string[] migrations)
    {
        using var c = Open();
        long version = (long)(Scalar(c, "PRAGMA user_version;") ?? 0L);
        for (int i = (int)version; i < migrations.Length; i++)
        {
            using var tx = c.BeginTransaction();
            Exec(c, migrations[i], tx);
            Exec(c, $"PRAGMA user_version={i + 1};", tx);
            tx.Commit();
        }
    }

    public int Execute(string sql, params (string name, object? value)[] args)
    {
        using var c = Open();
        return Exec(c, sql, null, args);
    }

    public T? Scalar<T>(string sql, params (string name, object? value)[] args)
    {
        using var c = Open();
        var v = Scalar(c, sql, args);
        if (v == null || v is DBNull) return default;
        return (T)Convert.ChangeType(v, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string name, object? value)[] args)
    {
        using var c = Open();
        using var cmd = Build(c, sql, null, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public void InTransaction(Action<SqliteConnection, SqliteTransaction> work)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        work(c, tx);
        tx.Commit();
    }

    public static int Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string name, object? value)[] args)
    {
        using var cmd = Build(c, sql, tx, args);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection c, string sql, params (string name, object? value)[] args)
    {
        using var cmd = Build(c, sql, null, args);
        return cmd.ExecuteScalar();
    }

    private static SqliteCommand Build(SqliteConnection c, string sql, SqliteTransaction? tx, (string name, object? value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static string? Str(SqliteDataReader r, string col) { int i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetString(i); }
    public static long Long(SqliteDataReader r, string col) { int i = r.GetOrdinal(col); return r.IsDBNull(i) ? 0 : r.GetInt64(i); }
    public static double Dbl(SqliteDataReader r, string col) { int i = r.GetOrdinal(col); return r.IsDBNull(i) ? 0 : r.GetDouble(i); }
    public static bool Bool(SqliteDataReader r, string col) => Long(r, col) != 0;
}
