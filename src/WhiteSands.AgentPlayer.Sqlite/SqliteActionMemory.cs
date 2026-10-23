using System.Text.Json;
using Microsoft.Data.Sqlite;
using WhiteSands.AgentPlayer.Memory;
using WhiteSands.AgentPlayer.Protocol;

namespace WhiteSands.AgentPlayer.Sqlite;

/// <summary>
/// SQLite log of actions one game process has already called.
/// Each session reads only its own rows, so a new run starts with an empty list.
/// </summary>
public sealed class SqliteActionMemory : IActionMemory
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public SqliteActionMemory(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={full}");
        _connection.Open();
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            pragma.ExecuteNonQuery();
        }

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS actions (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              session_id TEXT NOT NULL,
              character TEXT NOT NULL,
              action TEXT NOT NULL,
              args TEXT NOT NULL,
              ok INTEGER NOT NULL,
              error TEXT
            );
            CREATE INDEX IF NOT EXISTS actions_by_session ON actions(session_id, id);
            """;
        command.ExecuteNonQuery();
    }

    public void Record(string sessionId, string character, DoneAction entry)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO actions (session_id, character, action, args, ok, error)
                VALUES ($sessionId, $character, $action, $args, $ok, $error)
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$character", character);
            command.Parameters.AddWithValue("$action", entry.Action);
            command.Parameters.AddWithValue("$args", entry.Args.GetRawText());
            command.Parameters.AddWithValue("$ok", entry.Ok ? 1 : 0);
            command.Parameters.AddWithValue("$error", (object?)entry.Error ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<DoneAction> Done(string sessionId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT action, args, ok, error FROM (
                  SELECT id, action, args, ok, error FROM actions
                  WHERE session_id = $sessionId
                  ORDER BY id DESC
                  LIMIT $limit
                ) ORDER BY id ASC
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$limit", ActionMemory.MaxDone);
            var done = new List<DoneAction>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var args = ProtocolJson.Parse(reader.GetString(1));
                var error = reader.IsDBNull(3) ? null : reader.GetString(3);
                done.Add(new DoneAction(reader.GetString(0), args, reader.GetInt32(2) == 1, error));
            }

            return done;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }
}
