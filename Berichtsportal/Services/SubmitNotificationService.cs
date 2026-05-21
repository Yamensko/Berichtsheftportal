using Berichtsportal.Data;
using Berichtsportal.Enums;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;

namespace Berichtsportal.Services;

/// <summary>
/// Hintergrund-Service fuer verzögerte E-Mail-Benachrichtigungen an Mentoren.
///
/// Warum verzögert (30 Minuten)?
/// - Wenn ein Azubi einen Bericht einreicht, soll der Mentor benachrichtigt werden.
/// - Aber: Azubis reichen manchmal versehentlich ein und ziehen den Bericht gleich zurueck.
/// - Ohne Verzoegerung wuerde der Mentor eine unnoetige Mail bekommen.
/// - Loesung: 30 Minuten warten. Wenn der Azubi in der Zwischenzeit den Bericht
///   zurueck auf "Entwurf" setzt, wird die Benachrichtigung automatisch storniert.
///
/// Registriert als Singleton UND als HostedService (BackgroundService),
/// damit der Service durchgehend im Hintergrund laeuft und die gleiche Instanz
/// von ReportService.SubmitReportAsync() und SaveReportAsync() erreichbar ist.
///
/// Alternative Loesungen, die verworfen wurden:
/// - Datenbank-basierte Queue: Aufwaendiger, aber ueberlebt Server-Neustarts.
///   Fuer unser Szenario (kurze Delays, interne App) ueberdimensioniert.
/// - Hangfire/Quartz.NET: Externe Bibliotheken, die fuer diesen einfachen
///   Use Case unnoetige Komplexitaet hinzufuegen wuerden.
/// - Sofortiger Versand: Wuerde den "Einreichen-Rueckziehen"-Workflow nicht unterstuetzen.
/// </summary>
public sealed class SubmitNotificationService : BackgroundService
{
    /// <summary>Wartezeit, bevor die Benachrichtigung tatsaechlich versendet wird.</summary>
    private static readonly TimeSpan NotificationDelay = TimeSpan.FromMinutes(30);

    /// <summary>Wie oft der Hintergrund-Service pruefen soll, ob Benachrichtigungen faellig sind.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// In-Memory-Queue: Key = ReportId, Value = Benachrichtigungsdaten.
    /// ConcurrentDictionary weil:
    /// - Mehrere Blazor-Verbindungen gleichzeitig Schedule/Cancel aufrufen koennen
    /// - Der Background-Thread gleichzeitig faellige Items entfernt
    /// - Dictionary waere NICHT thread-safe und koennte zu Datenkorruption fuehren
    /// </summary>
    private readonly ConcurrentDictionary<int, PendingNotification> _pending = new();

    /// <summary>
    /// Factory fuer Dependency Injection Scopes.
    /// Noetig, weil ein Singleton (dieser Service) keine Scoped-Services (DbContext, MailService)
    /// direkt injiziert bekommen darf. Stattdessen wird pro Benachrichtigung ein eigener Scope erstellt.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    public SubmitNotificationService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Plant eine Benachrichtigung fuer den Mentor ein.
    /// Falls fuer denselben Report bereits eine geplant ist, wird sie ueberschrieben
    /// (Dictionary-Verhalten: gleicher Key = Update).
    /// </summary>
    public void Schedule(int reportId, string submitterUserId)
    {
        _pending[reportId] = new PendingNotification(reportId, submitterUserId, DateTime.UtcNow);
    }

    /// <summary>
    /// Storniert eine geplante Benachrichtigung.
    /// Wird aufgerufen, wenn der Azubi den Bericht zurueck auf "Entwurf" setzt.
    /// </summary>
    public void Cancel(int reportId)
    {
        _pending.TryRemove(reportId, out _);
    }

    /// <summary>
    /// Hauptschleife des BackgroundService. Laeuft so lange die App laeuft.
    /// Jede Minute wird geprueft, ob Benachrichtigungen faellig sind (30 Min. seit Schedule).
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CheckInterval, stoppingToken);

            // Alle Items finden, deren Wartezeit abgelaufen ist
            var dueItems = _pending
                .Where(p => DateTime.UtcNow >= p.Value.ScheduledAt + NotificationDelay)
                .Select(p => p.Value)
                .ToList();

            foreach (var item in dueItems)
            {
                _pending.TryRemove(item.ReportId, out _);
                await TrySendNotificationAsync(item);
            }
        }
    }

    /// <summary>
    /// Versucht die Benachrichtigung zu senden.
    /// Liest den Report nochmal aus der Datenbank, um sicherzustellen,
    /// dass er noch den Status "Submitted" hat (Double-Check).
    /// </summary>
    private async Task TrySendNotificationAsync(PendingNotification notification)
    {
        try
        {
            // Eigener Scope fuer DB-Zugriff und Mailversand
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<BerichtsportalDbContext>();
            var mailService = scope.ServiceProvider.GetRequiredService<MailService>();

            // Report mit Azubi und Mentor laden
            var report = await context.Reports
                .Include(r => r.Azubi)
                    .ThenInclude(a => a.Mentor)
                .FirstOrDefaultAsync(r => r.ReportId == notification.ReportId);

            // Sicherheitspruefungen: Nur senden wenn Report existiert UND noch eingereicht ist
            if (report == null || report.Status != ReportStatus.Submitted)
                return;

            if (report.Azubi.Mentor == null)
                return;

            // Fake-Principal erstellen, weil der MailService einen ClaimsPrincipal
            // mit der Object ID braucht (fuer den GraphTokenProvider)
            var principal = CreatePrincipal(notification.SubmitterUserId);

            // HTML-E-Mail zusammenbauen (mit XSS-Schutz durch HtmlEncode)
            var azubiName = WebUtility.HtmlEncode(
                $"{report.Azubi.FirstName} {report.Azubi.LastName}");

            var mailBody =
                $"<p>Hallo {WebUtility.HtmlEncode(report.Azubi.Mentor.FirstName)},</p>" +
                $"<p>{azubiName} hat den Wochenbericht " +
                $"KW {report.CalendarWeek}/{report.Year} eingereicht.</p>";

            await mailService.SendMailAsync(
                report.Azubi.Mentor.Email,
                $"Wochenbericht KW {report.CalendarWeek}/{report.Year} eingereicht",
                mailBody,
                principal);
        }
        catch
        {
            // Fehler beim Senden - NICHT den Background Service crashen lassen!
            // Lieber eine E-Mail nicht senden als den ganzen Service zu stoppen.
        }
    }

    /// <summary>
    /// Erstellt einen minimalen ClaimsPrincipal mit nur der Azure Object ID.
    /// Der MailService braucht diesen, um das Access Token vom GraphTokenProvider abzurufen.
    /// </summary>
    private static ClaimsPrincipal CreatePrincipal(string userId)
    {
        var claims = new[]
        {
            new Claim(
                "http://schemas.microsoft.com/identity/claims/objectidentifier",
                userId)
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "BackgroundService"));
    }

    /// <summary>Unveraenderlicher Datensatz fuer eine geplante Benachrichtigung.</summary>
    private sealed record PendingNotification(
        int ReportId,
        string SubmitterUserId,
        DateTime ScheduledAt);
}
