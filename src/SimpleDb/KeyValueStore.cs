using Microsoft.Data.Sqlite;

namespace SimpleDb;

public sealed class KeyValueStore : IDisposable
{
    private readonly object gate = new();
    private readonly SqliteConnection connection;

    public KeyValueStore(NodeOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        connection = new SqliteConnection($"Data Source={Path.Combine(options.DataDirectory, "values.db")};Pooling=False");
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS key_values (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public void Put(string key, string value)
    {
        lock (gate)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO key_values(key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    public string? Get(string key)
    {
        lock (gate)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM key_values WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void Dispose() => connection.Dispose();
}
