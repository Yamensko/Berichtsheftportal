using System.Collections.Concurrent;
using System.Text.Json;

namespace Berichtsportal.Services;

/// <summary>
/// Verwaltet Microsoft Graph API Access- und Refresh-Tokens pro Benutzer.
/// Registriert als Singleton, da die Tokens ueber alle Requests hinweg
/// im Arbeitsspeicher gehalten werden muessen (Blazor Server hat keine
/// klassischen HTTP-Sessions fuer Token-Speicherung).
///
/// Ablauf:
/// 1. Beim Login speichert OnTicketReceived die Tokens via StoreTokens().
/// 2. Eine Custom Middleware rehydriert die Tokens bei jedem Request aus dem Cookie.
/// 3. Bei Bedarf gibt GetAccessTokenAsync() ein gueltiges Token zurueck
///    und erneuert es automatisch, falls es bald ablaeuft.
/// </summary>
public sealed class GraphTokenProvider
{
    /// <summary>
    /// Thread-sicherer In-Memory-Speicher: Key = Azure Object ID des Benutzers.
    /// ConcurrentDictionary statt Dictionary, weil mehrere SignalR-Verbindungen
    /// gleichzeitig auf die Tokens zugreifen koennen.
    /// </summary>
    private readonly ConcurrentDictionary<string, TokenEntry> _tokens = new();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public GraphTokenProvider(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    /// <summary>
    /// Speichert ein Token-Paar fuer einen bestimmten Benutzer.
    /// Wird beim Login (OnTicketReceived) und von der Cookie-Middleware aufgerufen.
    /// </summary>
    public void StoreTokens(string userId, string accessToken, string refreshToken)
    {
        _tokens[userId] = new TokenEntry(accessToken, refreshToken);
    }

    /// <summary>
    /// Prueft, ob fuer den Benutzer Tokens vorhanden sind.
    /// Die Middleware nutzt dies, um zu entscheiden, ob eine Rehydrierung noetig ist.
    /// </summary>
    public bool HasTokens(string userId)
    {
        return _tokens.ContainsKey(userId);
    }

    /// <summary>
    /// Gibt ein gueltiges Access Token zurueck.
    /// Falls das Token innerhalb der naechsten 5 Minuten ablaeuft,
    /// wird es automatisch ueber den Refresh Token erneuert.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(string userId)
    {
        if (!_tokens.TryGetValue(userId, out var entry))
            throw new InvalidOperationException(
                "Kein Graph-Token vorhanden. Bitte abmelden, neu anmelden und erneut versuchen.");

        // Token ist noch gueltig -> direkt zurueckgeben
        if (!IsTokenExpiringSoon(entry.AccessToken))
            return entry.AccessToken;

        // Token laeuft bald ab -> automatisch erneuern
        var newEntry = await RefreshTokenAsync(entry.RefreshToken);
        _tokens[userId] = newEntry;
        return newEntry.AccessToken;
    }

    /// <summary>
    /// Prueft den exp-Claim im JWT-Payload (Base64-dekodiert),
    /// ob das Token innerhalb der naechsten 5 Minuten ablaeuft.
    /// Bei Fehler (ungueltiges Token) wird true zurueckgegeben,
    /// damit ein Refresh versucht wird.
    /// </summary>
    private static bool IsTokenExpiringSoon(string accessToken)
    {
        try
        {
            // JWT besteht aus 3 Teilen: Header.Payload.Signature
            var parts = accessToken.Split('.');
            if (parts.Length != 3) return true;

            // Payload mit Base64-Padding dekodieren
            var payload = parts[1];
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));

            // exp-Claim auslesen (Unix-Timestamp)
            using var doc = JsonDocument.Parse(json);
            var exp = doc.RootElement.GetProperty("exp").GetInt64();
            var expiry = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;

            // 5 Minuten Puffer, damit der Token nicht waehrend eines Requests ablaeuft
            return expiry <= DateTime.UtcNow.AddMinutes(5);
        }
        catch
        {
            // Bei Parse-Fehlern lieber erneuern als mit ungueltigem Token arbeiten
            return true;
        }
    }

    /// <summary>
    /// Fordert ein neues Access Token beim Azure-Token-Endpoint an,
    /// indem der Refresh Token als grant_type=refresh_token gesendet wird.
    /// Kein Client Secret noetig, da Public Client (SPA-Konfiguration).
    /// </summary>
    private async Task<TokenEntry> RefreshTokenAsync(string refreshToken)
    {
        var tenantId = _configuration["AzureAd:TenantId"];
        var clientId = _configuration["AzureAd:ClientId"];

        var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId!,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = "https://graph.microsoft.com/Mail.Send offline_access openid profile email"
        };

        var client = _httpClientFactory.CreateClient();
        using var response = await client.PostAsync(tokenEndpoint, new FormUrlEncodedContent(parameters));
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Token-Erneuerung fehlgeschlagen ({(int)response.StatusCode}). Bitte abmelden und neu anmelden.");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var newAccessToken = root.GetProperty("access_token").GetString()!;

        // Azure gibt nicht immer einen neuen Refresh Token zurueck.
        // Falls ja: verwenden; falls nein: alten behalten.
        var newRefreshToken = root.TryGetProperty("refresh_token", out var rt)
            ? rt.GetString()!
            : refreshToken;

        return new TokenEntry(newAccessToken, newRefreshToken);
    }

    /// <summary>
    /// Unveraenderlicher Datensatz fuer ein Token-Paar.
    /// Record statt Klasse, weil Tokens nach Erstellung nie geaendert werden (Immutability).
    /// </summary>
    private sealed record TokenEntry(string AccessToken, string RefreshToken);
}
