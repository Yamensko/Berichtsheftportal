using Berichtsportal.Enums;
using Berichtsportal.Models;
using Berichtsportal.Services;

namespace Berichtsportal.Tests;

public class PdfExportServiceTests
{
    [Fact]
    public async Task CreateCurrentUserReportsPdfAsync_ExportsAllCurrentUserReports()
    {
        await using var context = TestData.CreateContext();
        var ausbilder = TestData.CreateAusbilder();
        var azubi = TestData.CreateAzubi(mentorId: ausbilder.UserId);

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

        Assert.NotNull(result);
        Assert.Equal("Berichtsheft_Export_Alle.pdf", result!.FileName);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(result.Content[..5]));
        Assert.True(result.Content.Length > 500);
    }

    [Fact]
    public async Task CreateCurrentUserReportsPdfAsync_FiltersByDateRange()
    {
        await using var context = TestData.CreateContext();
        var azubi = TestData.CreateAzubi();

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
        var result = await service.CreateCurrentUserReportsPdfAsync(
            azubi,
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31));

        Assert.NotNull(result);
        Assert.Equal("Berichtsheft_Export_20260501_20260531.pdf", result!.FileName);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(result.Content[..5]));
    }

    [Fact]
    public async Task CreateCurrentUserReportsPdfAsync_DeniesAusbilderDirectCurrentUserExport()
    {
        await using var context = TestData.CreateContext();
        var ausbilder = TestData.CreateAusbilder();
        var service = new PdfExportService(context);

        var result = await service.CreateCurrentUserReportsPdfAsync(ausbilder, null, null);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateCurrentUserReportsPdfAsync_ExcludesDraftReports()
    {
        await using var context = TestData.CreateContext();
        var azubi = TestData.CreateAzubi();

        context.Users.Add(azubi);
        context.Reports.AddRange(
            new Report
            {
                AzubiId = azubi.UserId,
                Azubi = azubi,
                CalendarWeek = 10,
                Year = 2026,
                Content = "",
                Status = ReportStatus.Draft,
                LastModified = new DateTime(2026, 3, 6)
            },
            new Report
            {
                AzubiId = azubi.UserId,
                Azubi = azubi,
                CalendarWeek = 11,
                Year = 2026,
                Content = "<p>Inhalt vorhanden</p>",
                Status = ReportStatus.Submitted,
                LastModified = new DateTime(2026, 3, 13)
            },
            new Report
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

        // PDF stores text as hex-encoded Latin1, so we search for hex patterns
        var pdfAscii = System.Text.Encoding.ASCII.GetString(result!.Content);
        string ToHex(string s) => Convert.ToHexString(System.Text.Encoding.Latin1.GetBytes(s));

        // Draft-Bericht (KW 10) darf NICHT enthalten sein
        Assert.DoesNotContain(ToHex("KW 10"), pdfAscii);
        // Submitted (KW 11) und Approved (KW 12) muessen enthalten sein
        Assert.Contains(ToHex("KW 11"), pdfAscii);
        Assert.Contains(ToHex("KW 12"), pdfAscii);
        // Anzahl muss 2 sein (nur Submitted + Approved)
        Assert.Contains(ToHex("Anzahl Wochenberichte: 2"), pdfAscii);
    }
}
