using Berichtsportal.Services;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Berichtsportal.Tests;

public class GraphTokenProviderTests
{
    [Fact]
    public async Task GetAccessTokenAsync_ReturnsStoredToken_WhenNotExpired()
    {
        var tokenProvider = TestData.CreateTokenProvider();
        var userId = Guid.NewGuid().ToString();
        var jwt = TestData.CreateFakeJwt();

        tokenProvider.StoreTokens(userId, jwt, "refresh-abc");

        var result = await tokenProvider.GetAccessTokenAsync(userId);

        Assert.Equal(jwt, result);
    }

    [Fact]
    public async Task GetAccessTokenAsync_RefreshesToken_WhenExpired()
    {
        // Expired JWT (exp = 1 hour ago)
        var expiredJwt = CreateJwtWithExp(DateTimeOffset.UtcNow.AddHours(-1));

        // Fake token endpoint returns new tokens
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

        Assert.Equal(newAccessToken, result);
        Assert.Contains("old-refresh-token", handler.LastBody);
    }

    [Fact]
    public void HasTokens_ReturnsFalse_WhenNoTokensStored()
    {
        var tokenProvider = TestData.CreateTokenProvider();

        Assert.False(tokenProvider.HasTokens("unknown-user"));
    }

    [Fact]
    public void HasTokens_ReturnsTrue_AfterStoring()
    {
        var tokenProvider = TestData.CreateTokenProvider();
        var userId = Guid.NewGuid().ToString();

        tokenProvider.StoreTokens(userId, "a", "b");

        Assert.True(tokenProvider.HasTokens(userId));
    }

    [Fact]
    public async Task GetAccessTokenAsync_ThrowsWhenNoTokens()
    {
        var tokenProvider = TestData.CreateTokenProvider();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tokenProvider.GetAccessTokenAsync("unknown-user"));

        Assert.Contains("Graph-Token", ex.Message);
    }

    private static string CreateJwtWithExp(DateTimeOffset expiry)
    {
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}"));
        var payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{{\"exp\":{expiry.ToUnixTimeSeconds()}}}"));
        return $"{header}.{payload}.fakesig";
    }
}

/// <summary>
/// Returns a fixed JSON response for token refresh requests.
/// </summary>
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
