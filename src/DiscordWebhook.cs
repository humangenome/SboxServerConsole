using System.Text;
using System.Text.Json;

namespace SboxServerConsole;

// Discord-compatible webhook. Posts a single embed per call. URL optional —
// when null/empty the webhook is a no-op so callers don't need to branch.
// Failures (network, 4xx/5xx, timeout) are swallowed; supervision must never
// be blocked by a webhook outage.
public sealed class DiscordWebhook : IDisposable
{
    public const int ColorGreen = 0x2ECC71;
    public const int ColorYellow = 0xF1C40F;
    public const int ColorRed = 0xE74C3C;
    public const int ColorBlue = 0x3498DB;

    readonly string? _url;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public DiscordWebhook(string? url)
        => _url = string.IsNullOrWhiteSpace(url) ? null : url;

    public bool Enabled => _url is not null;

    public async Task SendAsync(string title, string description, int colorRgb)
    {
        if (_url is null) return;
        var sb = new StringBuilder(256);
        sb.Append("{\"embeds\":[{");
        sb.Append("\"title\":").Append(JsonSerializer.Serialize(title)).Append(',');
        sb.Append("\"description\":").Append(JsonSerializer.Serialize(description)).Append(',');
        sb.Append("\"color\":").Append(colorRgb).Append(',');
        sb.Append("\"timestamp\":").Append(JsonSerializer.Serialize(DateTime.UtcNow.ToString("o")));
        sb.Append("}]}");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _url)
            {
                Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
    }

    public void Dispose() => _http.Dispose();
}
