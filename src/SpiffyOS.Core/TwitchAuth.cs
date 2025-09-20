using System.Net.Http.Headers;
using System.Text.Json;

namespace SpiffyOS.Core;

public sealed class TwitchAuth
{
    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _tokenPath;

    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public TwitchAuth(HttpClient http, string clientId, string clientSecret, string tokenPath)
    {
        _http = http; _clientId = clientId; _clientSecret = clientSecret; _tokenPath = tokenPath;
        var dir = Path.GetDirectoryName(_tokenPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        TryLoad();
    }

    public async Task<bool> EnsureValidAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(AccessToken) || DateTimeOffset.UtcNow > ExpiresAt.AddMinutes(-5))
            return await RefreshAsync(ct);
        return true;
    }

    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(RefreshToken)) return false;
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = RefreshToken!
        };
        using var res = await _http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(form), ct);
        if (!res.IsSuccessStatusCode) return false;
        var payload = await JsonSerializer.DeserializeAsync<AuthResponse>(
    await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (payload is null) throw new InvalidOperationException("Auth refresh returned null JSON.");
        Apply(payload);
        Save();
        return true;
    }

    public void ApplyAuth(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(AccessToken))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
            req.Headers.Add("Client-Id", _clientId);
        }
    }

    private void Apply(AuthResponse p)
    {
        AccessToken = p.access_token;
        RefreshToken = p.refresh_token;
        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(p.expires_in);
    }

    private void Save()
    {
        File.WriteAllText(_tokenPath, JsonSerializer.Serialize(new Persist
        {
            access_token = AccessToken ?? "",
            refresh_token = RefreshToken ?? "",
            expires_at = ExpiresAt
        }));
    }

    private void TryLoad()
    {
        if (!File.Exists(_tokenPath)) return;
        var p = JsonSerializer.Deserialize<Persist>(File.ReadAllText(_tokenPath));
        if (p is null) return;
        AccessToken = p.access_token;
        RefreshToken = p.refresh_token;
        ExpiresAt = p.expires_at;
    }

    private record AuthResponse(string access_token, string refresh_token, int expires_in, string token_type);
    private class Persist { public string access_token { get; set; } = ""; public string refresh_token { get; set; } = ""; public DateTimeOffset expires_at { get; set; } }
}
