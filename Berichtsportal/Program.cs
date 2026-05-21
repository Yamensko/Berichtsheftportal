using Berichtsportal.Components;
using Berichtsportal.Data;
using Berichtsportal.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// ══════════════════════════════════════════════════════════════
// 1) DIENST-REGISTRIERUNG (Dependency Injection Container)
// ══════════════════════════════════════════════════════════════

// Blazor Server mit InteractiveServer-Rendermodus aktivieren.
// InteractiveServer bedeutet: Die Seiten werden auf dem Server gerendert
// und Benutzer-Interaktionen laufen ueber eine SignalR-Verbindung (WebSocket).
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Den Connection String aus der Konfiguration lesen.
// Falls lokale User-Secrets/Appsettings fehlen, bleibt die Anwendung auf der
// Projekt-Datenbank lauffaehig (Fallback auf LocalDB).
var connectionString =
    builder.Configuration.GetConnectionString("DatabaseConnection")
    ?? "Server=(localdb)\\mssqllocaldb;Database=BerichtsportalDB;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";

// Entity Framework Core: DbContextFactory statt einfachem DbContext registrieren.
// Die Factory wird von UserServices benoetigt, weil Blazor-Server-Komponenten
// laenger leben als ein klassischer HTTP-Request (Scoped-Lifetime-Problem).
// EnableRetryOnFailure: Automatische Wiederholung bei temporaeren SQL-Fehlern
// (z.B. Netzwerk-Timeouts), bis zu 5 Versuche mit max. 10 Sekunden Wartezeit.
builder.Services.AddDbContextFactory<BerichtsportalDbContext>(options =>
    options.UseSqlServer(
        connectionString,
        sqlOptions => sqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorNumbersToAdd: null)));

// ── Geschaeftslogik-Services (Scoped = pro Request/Verbindung eine Instanz) ──
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<UserServices>();
builder.Services.AddScoped<PdfExportService>();

// MailService: Scoped, weil er den ClaimsPrincipal des aktuellen Benutzers benoetigt.
builder.Services.AddScoped<MailService>();

// GraphTokenProvider: Singleton, weil Tokens ueber alle Requests hinweg
// im Arbeitsspeicher gehalten werden muessen (Blazor hat keine HTTP-Sessions).
builder.Services.AddSingleton<GraphTokenProvider>();

// SubmitNotificationService: Als Singleton UND als HostedService registriert.
// Singleton: Damit ReportService.SubmitReportAsync() und SaveReportAsync()
//            auf dieselbe Instanz zugreifen (Schedule/Cancel).
// HostedService: Damit die Hintergrund-Schleife (ExecuteAsync) automatisch startet.
// Der Trick mit GetRequiredService vermeidet eine doppelte Instanz-Erstellung.
builder.Services.AddSingleton<SubmitNotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SubmitNotificationService>());

// ══════════════════════════════════════════════════════════════
// 2) AUTHENTIFIZIERUNG (Microsoft Entra ID / OpenID Connect)
// ══════════════════════════════════════════════════════════════

// Origin-Header fuer den Backchannel-HttpClient.
// Noetig, weil die Azure-App als Public Client (SPA) konfiguriert ist
// und Azure den Origin-Header fuer CORS-Pruefung erwartet.
var publicClientOrigin = "https://localhost:7014";

// OpenID Connect mit PKCE (Proof Key for Code Exchange) konfigurieren.
// PKCE schuetzt den Authorization Code Flow gegen Abfangen des Codes.
// Kein ClientSecret noetig, weil Public Client.
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(options =>
    {
        // Azure AD-Einstellungen aus appsettings.json laden (TenantId, ClientId, etc.)
        builder.Configuration.Bind("AzureAd", options);

        options.ResponseType = "code";      // Authorization Code Flow (nicht Implicit)
        options.UsePkce = true;             // PKCE aktivieren (Sicherheit)
        options.SaveTokens = true;          // Tokens im Auth-Cookie speichern (fuer Middleware)

        // Zusaetzliche Berechtigungen anfordern:
        // Mail.Send: E-Mails ueber Graph API im Namen des Benutzers senden
        // offline_access: Refresh Token erhalten (fuer automatische Token-Erneuerung)
        options.Scope.Add("https://graph.microsoft.com/Mail.Send");
        options.Scope.Add("offline_access");

        // Backchannel mit Origin-Header (Public Client Workaround)
        var backchannel = new HttpClient();
        backchannel.DefaultRequestHeaders.Add("Origin", publicClientOrigin);
        options.Backchannel = backchannel;

        // Event: Wird nach erfolgreichem Login aufgerufen.
        // Hier werden die Tokens vom OIDC-Response extrahiert und im
        // GraphTokenProvider (Singleton, In-Memory) gespeichert.
        options.Events.OnTicketReceived = context =>
        {
            var accessToken = context.Properties?.GetTokenValue("access_token");
            var refreshToken = context.Properties?.GetTokenValue("refresh_token");

            if (!string.IsNullOrWhiteSpace(accessToken)
                && !string.IsNullOrWhiteSpace(refreshToken)
                && context.Principal?.Identity is ClaimsIdentity identity)
            {
                // Azure Object ID als Schluessel fuer den Token-Speicher
                var userId = identity.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                    ?? identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (!string.IsNullOrWhiteSpace(userId))
                {
                    var tokenProvider = context.HttpContext.RequestServices.GetRequiredService<GraphTokenProvider>();
                    tokenProvider.StoreTokens(userId, accessToken, refreshToken);
                }
            }

            return Task.CompletedTask;
        };
    });

// Autorisierung und kaskadierenden AuthenticationState fuer Blazor aktivieren.
// CascadingAuthenticationState: Blazor-Komponenten koennen den aktuellen Benutzer
// ueber [CascadingParameter] Task<AuthenticationState> abfragen.
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// HttpClient-Factory fuer MailService und GraphTokenProvider (Token-Refresh).
builder.Services.AddHttpClient();

// Microsoft Identity UI fuer Login/Logout-Endpunkte (MicrosoftIdentity/Account/SignIn etc.)
builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();

var app = builder.Build();

// ══════════════════════════════════════════════════════════════
// 3) MIDDLEWARE-PIPELINE (Reihenfolge ist wichtig!)
// ══════════════════════════════════════════════════════════════

// Fehlerbehandlung und HSTS nur in Produktion (Development zeigt Details)
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();  // Strict Transport Security: Browser erzwingt HTTPS fuer 30 Tage
}

app.UseHttpsRedirection();  // HTTP -> HTTPS Umleitung

// Authentifizierung und Autorisierung MUESSEN vor den Endpunkten stehen,
// damit [Authorize] und RequireAuthorization() wirken.
app.UseAuthentication();
app.UseAuthorization();

// ── Custom Middleware: Token-Rehydrierung ──
// Problem: GraphTokenProvider ist ein Singleton (In-Memory).
// Bei Server-Neustart gehen alle Tokens verloren, obwohl die Benutzer
// noch gueltige Auth-Cookies haben.
//
// Loesung: Bei jedem Request pruefen, ob der GraphTokenProvider die Tokens
// des Benutzers kennt. Falls nicht, werden sie aus dem Auth-Cookie extrahiert
// und im Provider wiederhergestellt ("rehydriert").
//
// Warum nicht einfach bei jedem Request aus dem Cookie lesen?
// -> Performance: Cookie-Entschluesselung ist teuer. HasTokens() ist ein
//    schneller Dictionary-Lookup, der in 99% der Faelle true liefert.
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var userId = context.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (!string.IsNullOrWhiteSpace(userId))
        {
            var tokenProvider = context.RequestServices.GetRequiredService<GraphTokenProvider>();

            // Nur rehydrieren, wenn der Provider den Benutzer NICHT kennt
            if (!tokenProvider.HasTokens(userId))
            {
                // Tokens aus dem verschluesselten Auth-Cookie extrahieren
                var authResult = await context.AuthenticateAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme);
                var accessToken = authResult.Properties?.GetTokenValue("access_token");
                var refreshToken = authResult.Properties?.GetTokenValue("refresh_token");

                if (!string.IsNullOrWhiteSpace(accessToken) && !string.IsNullOrWhiteSpace(refreshToken))
                {
                    tokenProvider.StoreTokens(userId, accessToken, refreshToken);
                }
            }
        }
    }

    await next(context);
});

app.UseAntiforgery();  // CSRF-Schutz fuer Formulare

app.MapStaticAssets();  // CSS, JS, Bilder aus wwwroot

app.MapControllers();   // Microsoft Identity UI Login/Logout-Endpunkte

// ══════════════════════════════════════════════════════════════
// 4) ENDPUNKTE
// ══════════════════════════════════════════════════════════════

// Minimal API Endpunkt fuer den PDF-Sammelexport.
// Warum kein Blazor-Component?
// -> Dateidownloads funktionieren in Blazor Server nicht direkt ueber SignalR.
//    Stattdessen wird ein normaler HTTP-GET-Endpunkt bereitgestellt,
//    den die Blazor-Komponente per <a href="/export/pdf"> verlinkt.
//
// Query-Parameter: ?from=2025-01-01&to=2025-06-30 (optional)
app.MapGet(
    "/export/pdf",
    async (
        HttpContext httpContext,
        ClaimsPrincipal principal,
        UserServices userServices,
        PdfExportService pdfExportService) =>
    {
        var currentUser = await userServices.GetCurrentUserAsync(principal);

        if (currentUser == null)
            return Results.Unauthorized();

        var fromDate = ParseDate(httpContext.Request.Query["from"]);
        var toDate = ParseDate(httpContext.Request.Query["to"]);
        var export = await pdfExportService.CreateCurrentUserReportsPdfAsync(currentUser, fromDate, toDate);

        if (export == null)
            return Results.NotFound();

        // Content-Disposition: attachment -> Browser laedt Datei herunter statt sie anzuzeigen
        return Results.File(export.Content, "application/pdf", export.FileName);
    })
    .RequireAuthorization();  // Nur fuer angemeldete Benutzer

/// <summary>Hilfsfunktion: Parst einen Datums-String sicher (ohne Exception bei ungueltigem Format).</summary>
static DateTime? ParseDate(string? value)
{
    if (DateTime.TryParse(value, out var date))
        return date;

    return null;
}

// Blazor-Komponenten als Razor-Pages bereitstellen (Hauptanwendung)
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

