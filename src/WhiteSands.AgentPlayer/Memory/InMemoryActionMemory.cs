namespace WhiteSands.AgentPlayer.Memory;

/// <summary>
/// In-memory log of actions. A new <see cref="AgentServer"/> starts empty.
/// The optional SQLite package writes the same rows to disk.
/// </summary>
public sealed class InMemoryActionMemory : IActionMemory
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();

    public void Record(string sessionId, string character, DoneAction entry)
    {
        lock (_gate)
        {
            _entries.Add(new Entry(sessionId, character, entry));
        }
    }

    public IReadOnlyList<DoneAction> Done(string sessionId)
    {
        lock (_gate)
        {
            var matches = new List<DoneAction>();
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].SessionId == sessionId)
                {
                    matches.Add(_entries[i].Action);
                }
            }

            if (matches.Count <= ActionMemory.MaxDone)
            {
                return matches;
            }

            return matches.GetRange(matches.Count - ActionMemory.MaxDone, ActionMemory.MaxDone);
        }
    }

    public void Dispose()
    {
    }

    private sealed class Entry
    {
        public Entry(string sessionId, string character, DoneAction action)
        {
            SessionId = sessionId;
            Character = character;
            Action = action;
        }

        public string SessionId { get; }

        public string Character { get; }

        public DoneAction Action { get; }
    }
}
