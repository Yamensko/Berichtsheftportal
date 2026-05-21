using Berichtsportal.Data;
using Berichtsportal.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Berichtsportal.Services
{
    /// <summary>
    /// Service fuer alle benutzerbezogenen Datenbankabfragen.
    ///
    /// Warum IDbContextFactory statt direktem DbContext?
    /// - Blazor Server haelt SignalR-Verbindungen lange offen (Minuten bis Stunden).
    /// - Ein einzelner DbContext wuerde ueber die gesamte Verbindung bestehen bleiben
    ///   und veraltete Daten liefern (Stale-Data-Problem).
    /// - Die Factory erstellt pro Abfrage einen frischen, kurzlebigen DbContext.
    /// - "await using" sorgt dafuer, dass jeder Context nach der Abfrage sofort entsorgt wird.
    ///
    /// Warum nutzen die anderen Services (ReportService, PdfExportService) keinen Factory?
    /// - Diese Services werden von HTTP-Requests oder kurzen Aktionen aufgerufen.
    /// - UserServices wird dagegen haeufig in lang laufenden Blazor-Komponenten verwendet,
    ///   wo ein Scoped-DbContext zu Problemen fuehren wuerde.
    ///
    /// Registriert als Scoped in Program.cs.
    /// </summary>
    public class UserServices
    {
        private readonly IDbContextFactory<BerichtsportalDbContext> dbContextFactory;

        public UserServices(IDbContextFactory<BerichtsportalDbContext> dbContextFactory)
        {
            this.dbContextFactory = dbContextFactory;
        }

        /// <summary>
        /// Laedt einen Benutzer anhand seiner GUID (Primaerschluessel).
        /// Wird z.B. verwendet, um den Mentor eines Azubis aufzuloesen.
        /// </summary>
        public async Task<User?> GetUserByIdAsync(Guid userId)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync();

            return await context.Users
                .FirstOrDefaultAsync(u => u.UserId == userId);
        }

        /// <summary>
        /// Laedt ALLE Azubis mit ihren Reports (fuer Admin-Uebersicht).
        /// Include(u => u.Reports) laedt die Reports direkt mit (Eager Loading),
        /// damit keine zusaetzlichen DB-Abfragen pro Azubi noetig sind (N+1-Problem).
        /// </summary>
        public async Task<List<User>> GetAzubisWithReportsAsync()
        {
            await using var context = await dbContextFactory.CreateDbContextAsync();

            return await context.Users
                .Where(u => u.Role.RoleName == "Auszubildender")
                .Include(u => u.Reports)
                .ToListAsync();
        }

        /// <summary>
        /// Laedt nur die Azubis, die einem bestimmten Ausbilder zugeordnet sind.
        /// Wird fuer das Ausbilder-Dashboard verwendet, damit jeder Ausbilder
        /// nur "seine" Azubis sieht.
        /// </summary>
        public async Task<List<User>> GetAzubisForAusbilderAsync(Guid AusbilderId)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync();

            return await context.Users
                .Where(u => u.MentorId == AusbilderId)
                .Include(u => u.Reports)
                .ToListAsync();
        }

        /// <summary>
        /// Sucht einen Benutzer anhand seiner E-Mail-Adresse.
        /// Laedt die Rolle mit (Include), weil sie fuer die Autorisierung benoetigt wird.
        /// Wird intern von GetCurrentUserAsync() aufgerufen.
        /// </summary>
        public async Task<User?> GetUserByEmailAsync(string email)
        {
            await using var context = await dbContextFactory.CreateDbContextAsync();

            return await context.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Email == email);
        }

        /// <summary>
        /// Ermittelt den aktuell angemeldeten Benutzer aus den Claims des Tokens.
        ///
        /// Die E-Mail-Adresse wird aus mehreren moeglichen Claims gesucht (Fallback-Kette):
        /// 1. ClaimTypes.Email         – Standard-E-Mail-Claim
        /// 2. "preferred_username"     – Haupt-Claim von Microsoft Entra ID
        /// 3. "upn"                    – User Principal Name (On-Premise-AD)
        /// 4. Identity.Name            – Letzter Fallback
        ///
        /// Warum diese Reihenfolge?
        /// - Microsoft Entra ID liefert je nach Konfiguration unterschiedliche Claims.
        /// - So funktioniert die Erkennung unabhaengig von der Azure-AD-Einrichtung.
        /// </summary>
        public async Task<User?> GetCurrentUserAsync(ClaimsPrincipal principal)
        {
            var email =
                principal.FindFirst(ClaimTypes.Email)?.Value
                ?? principal.FindFirst("preferred_username")?.Value
                ?? principal.FindFirst("upn")?.Value
                ?? principal.Identity?.Name;

            if (string.IsNullOrWhiteSpace(email))
                return null;

            return await GetUserByEmailAsync(email);
        }
    }
}
