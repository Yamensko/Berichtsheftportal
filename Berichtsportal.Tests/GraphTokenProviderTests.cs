using Berichtsportal.Services;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Berichtsportal.Tests;

/// Tests fuer den GraphTokenProvider (Token-Speicherung und automatische Erneuerung)
public class GraphTokenProviderTests
{
    /// Wenn ein gueltiges Token gespeichert ist, wird es direkt zurueckgegeben (kein Refresh)
    [Fact]
    public async Task GetAccessTokenAsync_ReturnsStoredToken_WhenNotExpired()
    {
        var tokenProvider = TestData.CreateTokenProvider();
        var userId = Guid.NewGuid().ToString();
        var jwt = TestData.CreateFakeJwt();  // Gueltig fuer 1 Stunde

        tokenProvider.StoreTokens(userId, jwt, "refresh-abc");

        var result = await tokenProvider.GetAccessTokenAsync(userId);

        // Gleiches Token zurueck = kein Refresh noetig
        Assert.Equal(jwt, result);
    }

    /// Wenn das Token abgelaufen ist, wird automatisch ein neues per Refresh Token geholt
    [Fact]
    public async Task GetAccessTokenAsync_RefreshesToken_WhenExpired()
    {
        // Abgelaufenes JWT (exp = vor 1 Stunde)
        var expiredJwt = CreateJwtWithExp(DateTimeOffset.UtcNow.AddHours(-1));

        // Fake-Antwort vom Azure Token-Endpoint mit neuem Token
        var newAccessToken = TestData.CreateFakeJwt();
        var responseBody = JsonSerializer.Serialize(new
        {
            access_token = newAccessToken,
            refresh_token = "new-refresh-token"
        });

        var handler = new FakeTokenEndpointHandler(responseBody);
        var httpFactory = new FakeHttpClientFactory(new HttpClient(handler));
        var tokenProvider = TestData.CreateTokenProvider(httpFactory);
        var userId = Guid.NewGuid().ToString();

        tokenProvider.StoreTokens(userId, expiredJwt, "old-refresh-token");

        var result = await tokenProvider.GetAccessTokenAsync(userId);

        // Neues Token zurueckgegeben, alter Refresh Token wurde gesendet
        Assert.Equal(newAccessToken, result);
        Assert.Contains("old-refresh-token", handler.LastBody);
    }

    /// HasTokens gibt false zurueck wenn kein Token fuer den User gespeichert ist
    [Fact]
    public void HasTokens_ReturnsFalse_WhenNoTokensStored()
    {
        var tokenProvider = TestData.CreateTokenProvider();

        Assert.False(tokenProvider.HasTokens("unknown-user"));
    }

    /// HasTokens gibt true zurueck nachdem Tokens gespeichert wurden
    [Fact]
    public void HasTokens_ReturnsTrue_AfterStoring()
    {
        var tokenProvider = TestData.CreateTokenProvider();
        var userId = Guid.NewGuid().ToString();

        tokenProvider.StoreTokens(userId, "a", "b");

        Assert.True(tokenProvider.HasTokens(userId));
    }

    /// Ohne gespeicherte Tokens muss eine hilfreiche Fehlermeldung kommen
    [Fact]
    public async Task GetAccessTokenAsync_ThrowsWhenNoTokens()
    {
        var tokenProvider = TestData.CreateTokenProvider();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tokenProvider.GetAccessTokenAsync("unknown-user"));

        Assert.Contains("Graph-Token", ex.Message);
    }

    /// Hilfsmethode: Erstellt ein JWT mit beliebigem Ablaufzeitpunkt
    private static string CreateJwtWithExp(DateTimeOffset expiry)
    {
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}"));
        var payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{{\"exp\":{expiry.ToUnixTimeSeconds()}}}"));
        return $"{header}.{payload}.fakesig";
    }
}

/// Fake-Handler fuer den Azure Token-Endpoint: Gibt eine feste JSON-Antwort zurueck
/// Wird benutzt um die automatische Token-Erneuerung zu testen
internal sealed class FakeTokenEndpointHandler : HttpMessageHandler
{
    private readonly string responseBody;

    public FakeTokenEndpointHandler(string responseBody)
    {
        this.responseBody = responseBody;
    }

    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content != null)
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        };
    }
}
