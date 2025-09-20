using System.Net.Http.Headers;
using System.Text.Json;

namespace SpiffyOS.Core;

public sealed class AppTokenProvider
{
    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _clientSecret;

    private string? _token;
    private DateTimeOffset _expiresAt;

    public AppTokenProvider(HttpClient http, string clientId, string clientSecret)
    {
        _http = http;
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    public async Task<string> GetAsync(CancellationToken ct)
    {
        if (_token is null || DateTimeOffset.UtcNow >= _expiresAt.AddMinutes(-5))
            await RefreshAsync(ct);
        return _token!;
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["grant_type"] = "client_credentials"
        });

        using var res = await _http.PostAsync("https://id.twitch.tv/oauth2/token", form, ct);
        res.EnsureSuccessStatusCode();

        var doc = JsonSerializer.Deserialize<Resp>(await res.Content.ReadAsStringAsync(ct))!;
        _token = doc.access_token;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(doc.expires_in);
    }

    public void Apply(HttpRequestMessage req, string token)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Client-Id", _clientId);
    }

    private record Resp(string access_token, int expires_in, string token_type);
}
