using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace Berichtsportal.Services;

/// <summary>
/// Versendet E-Mails ueber die Microsoft Graph API (Endpunkt /me/sendMail).
/// Nutzt delegierte Berechtigung (Mail.Send): Die E-Mail wird im Namen des
/// angemeldeten Benutzers versendet, nicht als App.
///
/// Registriert als Scoped, weil er pro Request arbeitet und den
/// ClaimsPrincipal des aktuellen Benutzers benoetigt.
/// </summary>
public class MailService
{
    /// <summary>Claim-Typ fuer den Graph Access Token (Legacy, wird nicht mehr direkt genutzt).</summary>
    public const string GraphAccessTokenClaimType = "graph_access_token";

    /// <summary>Claim-Typ fuer die Azure Object ID des Benutzers.</summary>
    public const string UserObjectIdClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly GraphTokenProvider tokenProvider;

    public MailService(IHttpClientFactory httpClientFactory, GraphTokenProvider tokenProvider)
    {
        this.httpClientFactory = httpClientFactory;
        this.tokenProvider = tokenProvider;
    }

    /// <summary>
    /// Versendet eine HTML-E-Mail ueber Microsoft Graph.
    /// Ablauf:
    /// 1. Access Token ueber GraphTokenProvider abrufen (automatische Erneuerung)
    /// 2. JSON-Payload fuer /me/sendMail zusammenbauen
    /// 3. POST-Request mit Bearer-Token an Graph API senden
    /// </summary>
    public async Task SendMailAsync(
        string to,
        string subject,
        string body,
        ClaimsPrincipal user)
    {
        var accessToken = await GetAccessTokenAsync(user);

        // Graph API erwartet ein bestimmtes JSON-Format fuer sendMail
        var payload = new
        {
            message = new
            {
                subject,
                body = new
                {
                    contentType = "HTML",
                    content = body
                },
                toRecipients = new[]
                {
                    new
                    {
                        emailAddress = new
                        {
                            address = to
                        }
                    }
                }
            },
            saveToSentItems = true  // E-Mail auch im Gesendet-Ordner speichern
        };

        using var request =
            new HttpRequestMessage(
                HttpMethod.Post,
                "https://graph.microsoft.com/v1.0/me/sendMail");

        // Delegierte Berechtigung: Bearer-Token des angemeldeten Benutzers
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        request.Content =
            new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

        var httpClient = httpClientFactory.CreateClient();
        using var response = await httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();

            throw new InvalidOperationException(
                $"E-Mail-Versand über Microsoft Graph fehlgeschlagen: {(int)response.StatusCode} {response.ReasonPhrase}. {error}");
        }
    }

    /// <summary>
    /// Vorab-Pruefung: Wirft eine Exception, falls kein Benutzer identifizierbar ist.
    /// Wird von ApproveReportAsync VOR der DB-Aenderung aufgerufen,
    /// damit die Genehmigung nicht gespeichert wird, wenn der Mailversand garantiert scheitern wuerde.
    /// </summary>
    public void EnsureCanSendMail(ClaimsPrincipal user)
    {
        var userId = GetUserId(user);
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException(
                "Kein Benutzer identifiziert. Bitte abmelden und neu anmelden.");
    }

    /// <summary>
    /// Holt das Access Token ueber den GraphTokenProvider.
    /// Der Provider erneuert das Token automatisch, falls es abgelaufen ist.
    /// </summary>
    private async Task<string> GetAccessTokenAsync(ClaimsPrincipal user)
    {
        var userId = GetUserId(user);

        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException(
                "Kein Benutzer identifiziert. Bitte abmelden und neu anmelden.");

        return await tokenProvider.GetAccessTokenAsync(userId);
    }

    /// <summary>
    /// Extrahiert die Azure Object ID des Benutzers aus den Claims.
    /// Fallback auf NameIdentifier, falls objectidentifier nicht vorhanden.
    /// </summary>
    private static string? GetUserId(ClaimsPrincipal user)
    {
        return user.FindFirst(UserObjectIdClaimType)?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
