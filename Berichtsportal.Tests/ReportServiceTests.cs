using Berichtsportal.Enums;
using Berichtsportal.Models;
using Berichtsportal.Services;
using Microsoft.EntityFrameworkCore;

namespace Berichtsportal.Tests;

public class ReportServiceTests
{
    [Fact]
    public async Task GetOrCreateReportAsync_CreatesReportOnlyOnce()
    {
        await using var context = TestData.CreateContext();
        var tokenProvider = TestData.CreateTokenProvider();
        var mailService = new MailService(new FakeHttpClientFactory(new HttpClient(new RecordingHttpMessageHandler())), tokenProvider);
        var service = new ReportService(context, mailService, TestData.CreateNotificationService());
        var userId = Guid.NewGuid();

        var first = await service.GetOrCreateReportAsync(userId, 2026, 20);
        var second = await service.GetOrCreateReportAsync(userId, 2026, 20);

        Assert.Equal(first.ReportId, second.ReportId);
        Assert.Equal(1, await context.Reports.CountAsync());
        Assert.Equal(ReportStatus.Draft, first.Status);
    }

    [Fact]
    public async Task ApproveReportAsync_SetsStatusAddsCommentAndSendsMail()
    {
        await using var context = TestData.CreateContext();
        var ausbilder = TestData.CreateAusbilder();
        var azubi = TestData.CreateAzubi(mentorId: ausbilder.UserId);
        var report = new Report
        {
            AzubiId = azubi.UserId,
            Azubi = azubi,
            CalendarWeek = 12,
            Year = 2026,
            Content = "<p>Test</p>",
            Status = ReportStatus.Submitted
        };

        context.Users.AddRange(ausbilder, azubi);
        context.Reports.Add(report);
        await context.SaveChangesAsync();

        var handler = new RecordingHttpMessageHandler();
        var httpFactory = new FakeHttpClientFactory(new HttpClient(handler));
        var tokenProvider = TestData.CreateTokenProvider(httpFactory);
        var userId = ausbilder.UserId.ToString();
        var fakeJwt = TestData.CreateFakeJwt();
        tokenProvider.StoreTokens(userId, fakeJwt, "refresh-123");
        var mailService = new MailService(httpFactory, tokenProvider);
        var service = new ReportService(context, mailService, TestData.CreateNotificationService());
        var principal = TestData.CreatePrincipal(objectId: userId);

        await service.ApproveReportAsync(report.ReportId, ausbilder.UserId, "Passt", principal);

        Assert.Equal(ReportStatus.Approved, report.Status);
        Assert.Single(context.Comments);
        Assert.Contains("freigegeben", handler.LastBody);
    }

    [Fact]
    public async Task RejectReportAsync_SetsStatusAndStoresComment()
    {
        await using var context = TestData.CreateContext();
        var ausbilder = TestData.CreateAusbilder();
        var azubi = TestData.CreateAzubi(mentorId: ausbilder.UserId);
        var report = new Report
        {
            AzubiId = azubi.UserId,
            Azubi = azubi,
            CalendarWeek = 13,
            Year = 2026,
            Content = "<p>Test</p>",
            Status = ReportStatus.Submitted
        };

        context.Users.AddRange(ausbilder, azubi);
        context.Reports.Add(report);
        await context.SaveChangesAsync();

        var tokenProvider = TestData.CreateTokenProvider();
        var mailService = new MailService(new FakeHttpClientFactory(new HttpClient(new RecordingHttpMessageHandler())), tokenProvider);
        var service = new ReportService(context, mailService, TestData.CreateNotificationService());

        await service.RejectReportAsync(report.ReportId, ausbilder.UserId, "Bitte ueberarbeiten");

        Assert.Equal(ReportStatus.Rejected, report.Status);
        Assert.Single(context.Comments);
        Assert.Equal("Bitte ueberarbeiten", context.Comments.Single().CommentText);
    }
}
