using Berichtsportal.Data;
using Berichtsportal.Enums;
using Berichtsportal.Models;
using Berichtsportal.Services;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Security.Claims;

namespace Berichtsportal.Services
{
    /// <summary>
    /// Zentraler Service fuer alle Report-bezogenen Geschaeftslogik-Operationen.
    /// Kapselt CRUD-Operationen, Statusuebergaenge und die zugehoerigen Seiteneffekte
    /// (E-Mail-Benachrichtigungen, Benachrichtigungs-Stornierung).
    ///
    /// Registriert als Scoped, weil er den DbContext (ebenfalls Scoped) benoetigt.
    /// </summary>
    public class ReportService
    {
        private readonly BerichtsportalDbContext context;
        private readonly MailService mailService;
        private readonly SubmitNotificationService notificationService;

        public ReportService(
            BerichtsportalDbContext context,
            MailService mailService,
            SubmitNotificationService notificationService)
        {
            this.context = context;
            this.mailService = mailService;
            this.notificationService = notificationService;
        }

        /// <summary>
        /// Lazy Creation: Holt einen bestehenden Report ODER erstellt ihn bei Bedarf.
        /// Dadurch werden keine leeren Reports vorab in der DB angelegt.
        ///
        /// Warum Lazy Creation?
        /// - Ein Azubi mit 3 Jahren Ausbildung haette sonst 156 leere Reports
        /// - Azubis die abbrechen wuerden verwaiste Datensaetze hinterlassen
        /// - Verlaengerungen der Ausbildung erfordern keine manuelle Anpassung
        /// </summary>
        public async Task<Report> GetOrCreateReportAsync(Guid userId, int year, int week)
        {
            var report = await context.Reports
                .FirstOrDefaultAsync(r =>
                    r.AzubiId == userId &&
                    r.Year == year &&
                    r.CalendarWeek == week);

            if (report == null)
            {
                report = new Report
                {
                    AzubiId = userId,
                    Year = year,
                    CalendarWeek = week,
                    Status = 0,           // Draft
                    Content = ""
                };

                context.Reports.Add(report);
                await context.SaveChangesAsync();
            }

            return report;
        }

        /// <summary>
        /// Laedt alle Reports eines Azubis fuer ein Halbjahr (KW 1-26 oder KW 27-52).
        /// Wird fuer die Kalenderansicht im Azubi-Dashboard verwendet.
        /// </summary>
        public async Task<List<Report>> GetReportsForPeriodAsync(Guid userId, int year, bool isSecondHalf)
        {
            int startWeek = isSecondHalf ? 27 : 1;
            int endWeek = startWeek + 25;

            return await context.Reports
                .Where(r =>
                    r.AzubiId == userId &&
                    r.Year == year &&
                    r.CalendarWeek >= startWeek &&
                    r.CalendarWeek <= endWeek)
                .ToListAsync();
        }

        /// <summary>
        /// Speichert einen Report (wird vom AutoSave-Timer aufgerufen).
        /// Wenn der Status auf Draft zurueckgesetzt wurde, wird eine eventuell
        /// geplante Mentor-Benachrichtigung automatisch storniert.
        /// </summary>
        public async Task SaveReportAsync(Report report)
        {
            // Azubi hat den Bericht zurueckgezogen -> Benachrichtigung abbrechen
            if (report.Status == ReportStatus.Draft)
                notificationService.Cancel(report.ReportId);

            context.Reports.Update(report);
            await context.SaveChangesAsync();
        }

        /// <summary>
        /// Setzt den Status auf "Submitted" und plant eine verzoegerte
        /// E-Mail-Benachrichtigung an den Mentor (30 Minuten Delay).
        /// </summary>
        public async Task SubmitReportAsync(int reportId, Guid azubiId, ClaimsPrincipal signedInUser)
        {
            var report = await context.Reports
                .FirstOrDefaultAsync(r => r.ReportId == reportId);

            // Sicherheitspruefung: Report muss existieren und dem Azubi gehoeren
            if (report == null || report.AzubiId != azubiId)
                return;

            report.Status = ReportStatus.Submitted;
            await context.SaveChangesAsync();

            // Azure Object ID des angemeldeten Benutzers fuer den GraphTokenProvider
            var userId =
                signedInUser.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                ?? signedInUser.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (!string.IsNullOrWhiteSpace(userId))
                notificationService.Schedule(reportId, userId);
        }

        /// <summary>
        /// Laedt einen Report mit allen Kommentaren (fuer die Detailansicht).
        /// </summary>
        public async Task<Report?> GetReportWithCommentsAsync(int reportId)
        {
            return await context.Reports
                .Include(r => r.Comments)
                .FirstOrDefaultAsync(r => r.ReportId == reportId);
        }

        /// <summary>
        /// Genehmigt einen Report (Ausbilder-Aktion).
        /// Ablauf:
        /// 1. Vorab pruefen, ob Mail gesendet werden kann (EnsureCanSendMail)
        /// 2. Status auf "Approved" setzen
        /// 3. Optional: Kommentar speichern
        /// 4. DB speichern
        /// 5. Benachrichtigungs-E-Mail an den Azubi senden
        ///
        /// Warum EnsureCanSendMail VOR der DB-Aenderung?
        /// -> Damit die Genehmigung nicht ohne E-Mail gespeichert wird.
        /// </summary>
        public async Task ApproveReportAsync(
            int reportId,
            Guid ausbilderId,
            string text,
            ClaimsPrincipal signedInUser)
        {
            var report = await context.Reports
                .Include(r => r.Azubi)
                .FirstOrDefaultAsync(r => r.ReportId == reportId);

            if (report == null)
                return;

            // Vorab-Pruefung: Kann der Ausbilder Mails senden?
            mailService.EnsureCanSendMail(signedInUser);

            report.Status = ReportStatus.Approved;

            // Optional: Ausbilder-Kommentar zur Genehmigung
            if (!string.IsNullOrWhiteSpace(text))
            {
                context.Comments.Add(new Comment
                {
                    ReportId = reportId,
                    CommentorId = ausbilderId,
                    CommentText = text,
                    CreatedAt = DateTime.Now
                });
            }

            await context.SaveChangesAsync();

            // E-Mail an den Azubi: "Dein Bericht wurde freigegeben"
            var azubiFirstName =
                WebUtility.HtmlEncode(report.Azubi.FirstName);
            var mailBody =
                $"<p>Hallo {azubiFirstName},</p>" +
                $"<p>dein Wochenbericht KW {report.CalendarWeek}/{report.Year} wurde freigegeben.</p>";

            await mailService.SendMailAsync(
                report.Azubi.Email,
                $"Wochenbericht KW {report.CalendarWeek}/{report.Year} freigegeben",
                mailBody,
                signedInUser
                );
        }

        /// <summary>
        /// Lehnt einen Report ab (Ausbilder-Aktion).
        /// Speichert einen Pflicht-Kommentar mit dem Ablehnungsgrund.
        /// Keine E-Mail, weil der Azubi den Status im Dashboard sehen kann.
        /// </summary>
        public async Task RejectReportAsync(int reportId, Guid ausbilderId, string text)
        {
            var report = await context.Reports
                .FirstOrDefaultAsync(r => r.ReportId == reportId);

            if (report == null)
                return;

            report.Status = ReportStatus.Rejected;

            context.Comments.Add(new Comment
            {
                ReportId = reportId,
                CommentorId = ausbilderId,
                CommentText = text,
                CreatedAt = DateTime.Now
            });

            await context.SaveChangesAsync();
        }

        /// <summary>
        /// Laedt einen Report anhand von Azubi + Woche (mit Kommentaren, neueste zuerst).
        /// Wird vom ReportEditor verwendet.
        /// </summary>
        public async Task<Report?> GetReportWithCommentsByWeekAsync(Guid userId, int year, int week)
        {
            return await context.Reports
                .Include(r => r.Comments.OrderByDescending(c => c.CreatedAt))
                .FirstOrDefaultAsync(r =>
                    r.AzubiId == userId &&
                    r.Year == year &&
                    r.CalendarWeek == week);
        }
    }

}
