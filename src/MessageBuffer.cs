namespace SboxServerConsole;

public sealed class MessageBuffer : IDisposable
{
    public readonly record struct Entry(long SeqNo, DateTime UtcAt, string Stream, string Line);

    readonly int _capacity;
    readonly LinkedList<Entry> _entries = new();
    readonly object _lock = new();
    long _seq;

    public event Action<Entry>? OnAppend;

    public MessageBuffer(int capacity) => _capacity = capacity;

    public void Append(string stream, string line)
    {
        Entry e;
        lock (_lock)
        {
            e = new Entry(++_seq, DateTime.UtcNow, stream, line);
            _entries.AddLast(e);
            while (_entries.Count > _capacity) _entries.RemoveFirst();
        }
        OnAppend?.Invoke(e);
    }

    public List<Entry> Tail(int count)
    {
        lock (_lock)
        {
            int take = Math.Min(count, _entries.Count);
            var list = new List<Entry>(take);
            int skip = _entries.Count - take;
            int i = 0;
            foreach (var e in _entries)
            {
                if (i++ < skip) continue;
                list.Add(e);
            }
            return list;
        }
    }

    public long LastSeq { get { lock (_lock) return _seq; } }

    public List<Entry> SinceSeq(long seq)
    {
        lock (_lock)
        {
            var list = new List<Entry>();
            foreach (var e in _entries)
            {
                if (e.SeqNo > seq) list.Add(e);
            }
            return list;
        }
    }

    public void Dispose()
    {
        lock (_lock) _entries.Clear();
    }
}
