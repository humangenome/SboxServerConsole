using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;

namespace SboxServerConsole;

// Scheduled command runner. Supports two schedule formats:
//   "@every 5m" / "@every 90s" / "@every 12h"
//   "0 */4 * * *"  (standard 5-field cron, parsed by Cronos)
// Persisted as a JSON file so jobs survive restarts. Tick loop is a single
// background thread that wakes every second; per-second resolution is plenty.
public sealed class Scheduler : IDisposable
{
    public sealed class Job
    {
        [JsonPropertyName("id")]           public string Id { get; set; } = "";
        [JsonPropertyName("schedule")]     public string Schedule { get; set; } = "";
        [JsonPropertyName("command")]      public string Command { get; set; } = "";
        [JsonPropertyName("enabled")]      public bool Enabled { get; set; } = true;
        [JsonPropertyName("created_at")]   public string CreatedAt { get; set; } = "";
        [JsonPropertyName("last_run_at")]  public string? LastRunAt { get; set; }
        [JsonPropertyName("run_count")]    public long RunCount { get; set; }
    }

    sealed class SchedulerFile
    {
        [JsonPropertyName("jobs")] public List<Job> Jobs { get; set; } = new();
    }

    readonly string? _path;
    readonly ServerProcess _server;
    readonly MessageBuffer _buffer;
    readonly AuditLog _audit;
    readonly object _lock = new();
    readonly Dictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _nextFire = new(StringComparer.OrdinalIgnoreCase);
    readonly CancellationTokenSource _cts = new();
    Thread? _thread;

    public Scheduler(CliConfig cfg, ServerProcess server, MessageBuffer buffer, AuditLog audit)
    {
        _path = string.IsNullOrWhiteSpace(cfg.SchedulerPath) ? null : cfg.SchedulerPath;
        _server = server;
        _buffer = buffer;
        _audit = audit;
        Load();
        RecomputeAllNext();
    }

    public bool Persisted => _path is not null;

    public void Start()
    {
        _thread = new Thread(Tick) { IsBackground = true, Name = "scheduler" };
        _thread.Start();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _thread?.Join(2000); } catch { }
    }

    public IReadOnlyList<Job> All()
    {
        lock (_lock) return _jobs.Values.OrderBy(j => j.Id).ToList();
    }

    public bool TryAdd(string id, string schedule, string command, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(id)) { error = "id required"; return false; }
        if (string.IsNullOrWhiteSpace(schedule)) { error = "schedule required"; return false; }
        if (string.IsNullOrWhiteSpace(command)) { error = "command required"; return false; }
        if (!TryComputeNext(schedule, DateTime.UtcNow, out _, out var perr)) { error = perr; return false; }
        var job = new Job
        {
            Id = id,
            Schedule = schedule,
            Command = command,
            Enabled = true,
            CreatedAt = DateTime.UtcNow.ToString("o"),
        };
        lock (_lock)
        {
            _jobs[id] = job;
            _nextFire[id] = ComputeNextOrFar(schedule, DateTime.UtcNow);
            Save();
        }
        _audit.Record("scheduler_add", new Dictionary<string, object?>
        {
            ["id"] = id, ["schedule"] = schedule, ["command"] = command,
        });
        return true;
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _jobs.Remove(id);
            _nextFire.Remove(id);
            if (removed) Save();
        }
        if (removed) _audit.Record("scheduler_remove", new Dictionary<string, object?> { ["id"] = id });
        return removed;
    }

    public bool SetEnabled(string id, bool enabled)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var j)) return false;
            j.Enabled = enabled;
            if (enabled) _nextFire[id] = ComputeNextOrFar(j.Schedule, DateTime.UtcNow);
            Save();
        }
        _audit.Record(enabled ? "scheduler_enable" : "scheduler_disable",
            new Dictionary<string, object?> { ["id"] = id });
        return true;
    }

    void Tick()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                List<Job>? toRun = null;
                lock (_lock)
                {
                    foreach (var (id, when) in _nextFire)
                    {
                        if (!_jobs.TryGetValue(id, out var j) || !j.Enabled) continue;
                        if (when <= now) (toRun ??= new()).Add(j);
                    }
                }
                if (toRun is not null)
                {
                    foreach (var j in toRun) Fire(j, now);
                }
            }
            catch (Exception ex) { _buffer.Append("system", $"scheduler tick error: {ex.Message}"); }
            try { Thread.Sleep(1000); } catch { break; }
        }
    }

    void Fire(Job j, DateTime now)
    {
        bool sent = _server.TrySendCommand(j.Command);
        _audit.Record("scheduler_fire", new Dictionary<string, object?>
        {
            ["id"] = j.Id, ["command"] = j.Command, ["sent"] = sent,
        });
        lock (_lock)
        {
            j.LastRunAt = now.ToString("o");
            j.RunCount++;
            _nextFire[j.Id] = ComputeNextOrFar(j.Schedule, now.AddSeconds(1));
            Save();
        }
    }

    void RecomputeAllNext()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            _nextFire.Clear();
            foreach (var j in _jobs.Values)
            {
                _nextFire[j.Id] = ComputeNextOrFar(j.Schedule, now);
            }
        }
    }

    static DateTime ComputeNextOrFar(string schedule, DateTime fromUtc)
    {
        if (TryComputeNext(schedule, fromUtc, out var dt, out _)) return dt;
        return DateTime.MaxValue;
    }

    public static bool TryComputeNext(string schedule, DateTime fromUtc, out DateTime nextUtc, out string error)
    {
        nextUtc = DateTime.MaxValue;
        error = "";
        var s = schedule.Trim();
        if (s.StartsWith("@every ", StringComparison.OrdinalIgnoreCase))
        {
            var spec = s[7..].Trim();
            if (!TryParseDuration(spec, out var span)) { error = $"invalid duration: {spec}"; return false; }
            if (span < TimeSpan.FromSeconds(1)) { error = "minimum @every is 1s"; return false; }
            nextUtc = fromUtc + span;
            return true;
        }
        try
        {
            var expr = CronExpression.Parse(s);
            var next = expr.GetNextOccurrence(fromUtc);
            if (next is null) { error = "cron expression has no future occurrence"; return false; }
            nextUtc = next.Value;
            return true;
        }
        catch (CronFormatException ex)
        {
            error = $"invalid cron: {ex.Message}";
            return false;
        }
    }

    static bool TryParseDuration(string s, out TimeSpan span)
    {
        span = TimeSpan.Zero;
        if (s.Length < 2) return false;
        char unit = char.ToLowerInvariant(s[^1]);
        if (!int.TryParse(s.AsSpan(0, s.Length - 1), out int n) || n <= 0) return false;
        span = unit switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            _ => TimeSpan.Zero,
        };
        return span > TimeSpan.Zero;
    }

    void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("jobs", out var arr)) return;
            foreach (var el in arr.EnumerateArray())
            {
                var j = new Job
                {
                    Id = el.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "",
                    Schedule = el.TryGetProperty("schedule", out var s) ? s.GetString() ?? "" : "",
                    Command = el.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "",
                    Enabled = !el.TryGetProperty("enabled", out var e) || e.GetBoolean(),
                    CreatedAt = el.TryGetProperty("created_at", out var ca) ? ca.GetString() ?? "" : "",
                    LastRunAt = el.TryGetProperty("last_run_at", out var lr) ? lr.GetString() : null,
                    RunCount = el.TryGetProperty("run_count", out var rc) ? rc.GetInt64() : 0,
                };
                if (!string.IsNullOrEmpty(j.Id)) _jobs[j.Id] = j;
            }
        }
        catch (Exception ex)
        {
            _buffer.Append("system", $"scheduler load failed: {ex.Message}");
        }
    }

    void Save()
    {
        if (_path is null) return;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var data = new SchedulerFile { Jobs = _jobs.Values.OrderBy(j => j.Id).ToList() };
        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, opts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _buffer.Append("system", $"scheduler save failed: {ex.Message}");
        }
    }

    public DateTime NextFireFor(string id)
    {
        lock (_lock) return _nextFire.TryGetValue(id, out var when) ? when : DateTime.MaxValue;
    }
}
