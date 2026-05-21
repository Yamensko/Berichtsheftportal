using Berichtsportal.Services;
using System.Net.Http.Headers;

namespace Berichtsportal.Tests;

public class MailServiceTests
{
    [Fact]
    public async Task SendMailAsync_UsesDelegatedGraphTokenAndMeEndpoint()
    {
        var handler = new RecordingHttpMessageHandler();
        var client = new HttpClient(handler);
        var httpFactory = new FakeHttpClientFactory(client);
        var tokenProvider = TestData.CreateTokenProvider(httpFactory);
        var objectId = Guid.NewGuid().ToString();
        var fakeJwt = TestData.CreateFakeJwt();
        tokenProvider.StoreTokens(objectId, fakeJwt, "refresh-123");
        var service = new MailService(httpFactory, tokenProvider);
        var principal = TestData.CreatePrincipal(objectId: objectId);

        await service.SendMailAsync(
            "empfaenger@pica.de",
            "Bericht freigegeben",
            "<p>Hallo</p>",
            principal);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/sendMail", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", fakeJwt), handler.LastRequest.Headers.Authorization);
        Assert.Contains("empfaenger@pica.de", handler.LastBody);
        Assert.Contains("Bericht freigegeben", handler.LastBody);
    }

    [Fact]
    public async Task SendMailAsync_WithoutTokens_ThrowsHelpfulMessage()
    {
        var handler = new RecordingHttpMessageHandler();
        var client = new HttpClient(handler);
        var httpFactory = new FakeHttpClientFactory(client);
        var tokenProvider = TestData.CreateTokenProvider(httpFactory);
        var service = new MailService(httpFactory, tokenProvider);
        var principal = TestData.CreatePrincipal(objectId: Guid.NewGuid().ToString());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendMailAsync("empfaenger@pica.de", "Betreff", "Body", principal));

        Assert.Contains("Graph-Token", exception.Message);
        Assert.Null(handler.LastRequest);
    }
}
