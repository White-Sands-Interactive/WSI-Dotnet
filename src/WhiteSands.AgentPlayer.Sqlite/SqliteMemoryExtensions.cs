using WhiteSands.AgentPlayer.Sqlite;

namespace WhiteSands.AgentPlayer;

public static class SqliteMemoryExtensions
{
    /// <summary>
    /// Store the action log in a SQLite file. Later views include that session's <c>done</c> list.
    /// Defaults to <c>.wsi/agent-memory.sqlite</c> in the current directory.
    /// </summary>
    public static AgentServerOptions UseSqliteMemory(this AgentServerOptions options, string path = ".wsi/agent-memory.sqlite")
    {
        options.Memory = new SqliteActionMemory(path);
        options.DisableMemory = false;
        return options;
    }
}
