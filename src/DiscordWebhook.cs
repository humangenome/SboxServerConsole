using System.Text;
using System.Text.Json;

namespace SboxServerConsole;

// Discord-compatible webhook. Posts a single embed per call. URL optional:
// when null/empty the webhook is a no-op so callers don't need to branch.
// Supervision must never wait on a webhook outage, so sends are fire-and-forget,
// but a webhook that stops working is reported once on the console (and once
// more when it works again) instead of failing silently forever.
public sealed class DiscordWebhook : IDisposable
{
    public const int ColorGreen = 0x2ECC71;
    public const int ColorYellow = 0xF1C40F;
    public const int ColorRed = 0xE74C3C;
    public const int ColorBlue = 0x3498DB;

    readonly string? _url;
    readonly Action<string>? _log;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    int _failing;

    public DiscordWebhook(string? url, Action<string>? log = null)
    {
        _url = string.IsNullOrWhiteSpace(url) ? null : url;
        _log = log;
    }

    public bool Enabled => _url is not null;

    // Shows player-controlled text as inline code. Backticks are removed so a name
    // cannot close the code span early.
    public static string Quote(string? text)
    {
        var t = (text ?? "").Replace("`", "'").Replace('\r', ' ').Replace('\n', ' ');
        if (t.Length > 80) t = t[..80];
        return "`" + t + "`";
    }

    public async Task SendAsync(string title, string description, int colorRgb)
    {
        if (_url is null) return;
        var sb = new StringBuilder(256);
        sb.Append("{\"embeds\":[{");
        sb.Append("\"title\":").Append(JsonSerializer.Serialize(title)).Append(',');
        sb.Append("\"description\":").Append(JsonSerializer.Serialize(description)).Append(',');
        sb.Append("\"color\":").Append(colorRgb).Append(',');
        sb.Append("\"timestamp\":").Append(JsonSerializer.Serialize(DateTime.UtcNow.ToString("o")));
        // Player names are user-controlled; never let one ping @everyone or a role.
        sb.Append("}],\"allowed_mentions\":{\"parse\":[]}}");
        string? problem = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _url)
            {
                Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            // 429 is Discord's rate limit during a burst of joins; not a broken webhook.
            if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 429)
                problem = $"HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            problem = ex.GetType().Name == nameof(TaskCanceledException) ? "timed out" : ex.Message;
        }

        if (problem is not null)
        {
            if (Interlocked.Exchange(ref _failing, 1) == 0)
                _log?.Invoke($"warn: Discord webhook failed ({problem}); notifications are not being delivered");
        }
        else if (Interlocked.Exchange(ref _failing, 0) == 1)
        {
            _log?.Invoke("Discord webhook is delivering again");
        }
    }

    public void Dispose() => _http.Dispose();
}
