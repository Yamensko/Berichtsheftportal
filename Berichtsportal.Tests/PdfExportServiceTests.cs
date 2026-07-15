using Berichtsportal.Enums;
using Berichtsportal.Models;
using Berichtsportal.Services;

namespace Berichtsportal.Tests;

/// Tests fuer den PDF-Export (Generierung ohne externe Bibliotheken, direkt nach PDF-1.4)
public class PdfExportServiceTests
{
    /// Exportiert alle Berichte eines Azubis als PDF und prueft Dateiname und PDF-Header
    [Fact]
    public async Task CreateCurrentUserReportsPdfAsync_ExportsAllCurrentUserReports()
    {
        await using var context = TestData.CreateContext();
        var ausbilder = TestData.CreateAusbilder();
        var azubi = TestData.CreateAzubi(mentorId: ausbilder.UserId);

        // Zwei Berichte anlegen (KW 10 und KW 11)
        context.Users.AddRange(ausbilder, azubi);
        context.Reports.AddRange(
            new Report
            {
                AzubiId = azubi.UserId,
                Azubi = azubi,
                CalendarWeek = 10,
                Year = 2026,
                Content = "<p>Projektarbeit</p>",
                Status = ReportStatus.Approved,
                LastModified = new DateTime(2026, 3, 6)
            },
            new Report
            {
                AzubiId = azubi.UserId,
                Azubi = azubi,
                CalendarWeek = 11,
                Year = 2026,
                Content = "<p>Dokumentation</p>",
                Status = ReportStatus.Submitted,
                LastModified = new DateTime(2026, 3, 13)
            });
        await context.SaveChangesAsync();

        var service = new PdfExportService(context);
        var result = await service.CreateCurrentUserReportsPdfAsync(azubi, null, null);

        // PDF wurde erstellt, Dateiname stimmt, Datei beginnt mit %PDF- (gueltige PDF)
        Assert.NotNull(result);
        Assert.Equal("Berichtsheft_Export_Alle.pdf", result!.FileName);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(result.Content[..5]));
        Assert.True(result.Content.Length > 500);
    }

    /// Prueft ob der Datumsfilter nur Berichte im angegebenen Zeitraum exportiert
    [Fact]
    public async Task CreateCurrentUserReportsPdfAsync_FiltersByDateRange()
    {
        await using var context = TestData.CreateContext();
        var azubi = TestData.CreateAzubi();

        // KW 1 (Januar) und KW 20 (Mai)
        context.Users.Add(azubi);
        context.Reports.AddRange(
            new Report
            {
                AzubiId = azubi.UserId,
                Azubi = azubi,
                CalendarWeek = 1,
                Year = 2026,
                Content = "<p>Januar</p>",
                Status = ReportStatus.Draft
            },
            new Report
            {
                AzubiId = azubi.UserId,
                Azubi = azubi,
                CalendarWeek = 20,
                Year = 2026,
                Content = "<p>Mai</p>",
                Status = ReportStatus.Draft
            });
        await context.SaveChangesAsync();

        var service = new PdfExportService(context);
        // Nur Mai-Berichte exportieren
        var result = await service.CreateCurrentUserReportsPdfAsync(
            azubi,
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31));

        // Dateiname enthaelt den Zeitraum, Datei ist eine gueltige PDF
        Assert.NotNull(result);
        Assert.Equal("Berichtsheft_Export_20260501_20260531.pdf", result!.FileName);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(result.Content[..5]));
    }

    /// Ausbilder duerfen keinen eigenen Export erstellen (nur Azubis)
    [Fact]
    public async Task CreateCurrentUserReportsPdfAsync_DeniesAusbilderDirectCurrentUserExport()
    {
        await using var context = TestData.CreateContext();
        var ausbilder = TestData.CreateAusbilder();
        var service = new PdfExportService(context);

        var result = await service.CreateCurrentUserReportsPdfAsync(ausbilder, null, null);

        // null zurueckgegeben = Export verweigert
        Assert.Null(result);
    }

    /// Draft-Berichte (Entwuerfe) werden NICHT exportiert, nur Submitted und Approved
    [Fact]
    public async Task CreateCurrentUserReportsPdfAsync_ExcludesDraftReports()
    {
        await using var context = TestData.CreateContext();
        var azubi = TestData.CreateAzubi();

        context.Users.Add(azubi);
        context.Reports.AddRange(
            new Report  // Draft = wird NICHT exportiert
            {
                AzubiId = azubi.UserId,
                Azubi = azubi,
                CalendarWeek = 10,
                Year = 2026,
                Content = "",
                Status = ReportStatus.Draft,
                LastModified = new DateTime(2026, 3, 6)
            },
            new Report  // Submitted = wird exportiert
            {
                AzubiId = azubi.UserId,
                Azubi = azubi,
                CalendarWeek = 11,
                Year = 2026,
                Content = "<p>Inhalt vorhanden</p>",
                Status = ReportStatus.Submitted,
                LastModified = new DateTime(2026, 3, 13)
            },
            new Report  // Approved = wird exportiert
            {
                AzubiId = azubi.UserId,
                Azubi = azubi,
                CalendarWeek = 12,
                Year = 2026,
                Content = "<p>Freigegeben</p>",
                Status = ReportStatus.Approved,
                LastModified = new DateTime(2026, 3, 20)
            });
        await context.SaveChangesAsync();

        var service = new PdfExportService(context);
        var result = await service.CreateCurrentUserReportsPdfAsync(azubi, null, null);

        Assert.NotNull(result);

        // PDF speichert Text als Hex-codiertes Latin1, daher Hex-Suche
        var pdfAscii = System.Text.Encoding.ASCII.GetString(result!.Content);
        string ToHex(string s) => Convert.ToHexString(System.Text.Encoding.Latin1.GetBytes(s));

        // KW 10 (Draft) darf NICHT im PDF stehen
        Assert.DoesNotContain(ToHex("KW 10"), pdfAscii);
        // KW 11 (Submitted) und KW 12 (Approved) muessen enthalten sein
        Assert.Contains(ToHex("KW 11"), pdfAscii);
        Assert.Contains(ToHex("KW 12"), pdfAscii);
        // Gesamtanzahl = 2 (ohne Draft)
        Assert.Contains(ToHex("Anzahl Wochenberichte: 2"), pdfAscii);
    }
}
