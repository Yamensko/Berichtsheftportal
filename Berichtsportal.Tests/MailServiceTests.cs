using Berichtsportal.Services;
using System.Net.Http.Headers;

namespace Berichtsportal.Tests;

/// Tests fuer den MailService (E-Mail-Versand ueber Microsoft Graph API)
public class MailServiceTests
{
    /// Prueft ob die Mail korrekt an den Graph-Endpoint /me/sendMail gesendet wird
    /// mit dem richtigen Bearer-Token, Empfaenger und Betreff
    [Fact]
    public async Task SendMailAsync_UsesDelegatedGraphTokenAndMeEndpoint()
    {
        // Fake HTTP-Handler zum Aufzeichnen des API-Aufrufs
        var handler = new RecordingHttpMessageHandler();
        var client = new HttpClient(handler);
        var httpFactory = new FakeHttpClientFactory(client);
        var tokenProvider = TestData.CreateTokenProvider(httpFactory);
        var objectId = Guid.NewGuid().ToString();
        var fakeJwt = TestData.CreateFakeJwt();
        // Token fuer den Benutzer speichern (simuliert erfolgreichen Login)
        tokenProvider.StoreTokens(objectId, fakeJwt, "refresh-123");
        var service = new MailService(httpFactory, tokenProvider);
        var principal = TestData.CreatePrincipal(objectId: objectId);

        await service.SendMailAsync(
            "empfaenger@pica.de",
            "Bericht freigegeben",
            "<p>Hallo</p>",
            principal);

        // Pruefe: POST an /me/sendMail, Bearer-Token korrekt, Empfaenger und Betreff im Body
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/sendMail", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", fakeJwt), handler.LastRequest.Headers.Authorization);
        Assert.Contains("empfaenger@pica.de", handler.LastBody);
        Assert.Contains("Bericht freigegeben", handler.LastBody);
    }

    /// Wenn kein Token gespeichert ist, muss eine hilfreiche Fehlermeldung kommen
    /// (nicht einfach NullReferenceException)
    [Fact]
    public async Task SendMailAsync_WithoutTokens_ThrowsHelpfulMessage()
    {
        var handler = new RecordingHttpMessageHandler();
        var client = new HttpClient(handler);
        var httpFactory = new FakeHttpClientFactory(client);
        var tokenProvider = TestData.CreateTokenProvider(httpFactory);
        var service = new MailService(httpFactory, tokenProvider);
        // Principal OHNE gespeichertes Token
        var principal = TestData.CreatePrincipal(objectId: Guid.NewGuid().ToString());

        // Muss InvalidOperationException mit "Graph-Token" im Text werfen
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendMailAsync("empfaenger@pica.de", "Betreff", "Body", principal));

        Assert.Contains("Graph-Token", exception.Message);
        // Kein HTTP-Request darf gesendet worden sein
        Assert.Null(handler.LastRequest);
    }
}
