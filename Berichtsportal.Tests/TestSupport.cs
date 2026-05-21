using Berichtsportal.Data;
using Berichtsportal.Models;
using Berichtsportal.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Security.Claims;

namespace Berichtsportal.Tests;

internal static class TestData
{
    public static BerichtsportalDbContext CreateContext(string? name = null)
    {
        var options = new DbContextOptionsBuilder<BerichtsportalDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .Options;

        return new BerichtsportalDbContext(options);
    }

    public static Role AzubiRole() => new() { RoleId = 1, RoleName = "Auszubildender" };

    public static Role AusbilderRole() => new() { RoleId = 2, RoleName = "Ausbilder" };

    public static User CreateAusbilder(Guid? id = null) => new()
    {
        UserId = id ?? Guid.NewGuid(),
        Email = "ausbilder@pica.de",
        FirstName = "Ada",
        LastName = "Ausbilder",
        RoleId = 2,
        Role = AusbilderRole()
    };

    public static User CreateAzubi(Guid? id = null, Guid? mentorId = null) => new()
    {
        UserId = id ?? Guid.NewGuid(),
        Email = "azubi@pica.de",
        FirstName = "Max",
        LastName = "Azubi",
        RoleId = 1,
        Role = AzubiRole(),
        MentorId = mentorId,
        AusbildungBeginn = new DateTime(2026, 1, 1),
        AusbildungEnde = new DateTime(2028, 12, 31)
    };

    public static ClaimsPrincipal CreatePrincipal(string email = "azubi@pica.de", string? graphToken = null, string? objectId = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Email, email),
            new("preferred_username", email)
        };

        if (!string.IsNullOrWhiteSpace(graphToken))
            claims.Add(new Claim(MailService.GraphAccessTokenClaimType, graphToken));

        if (!string.IsNullOrWhiteSpace(objectId))
            claims.Add(new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", objectId));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    public static SubmitNotificationService CreateNotificationService()
    {
        var scopeFactory = new FakeScopeFactory();
        return new SubmitNotificationService(scopeFactory);
    }

    public static GraphTokenProvider CreateTokenProvider(IHttpClientFactory? httpClientFactory = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAd:TenantId"] = "test-tenant",
                ["AzureAd:ClientId"] = "test-client"
            })
            .Build();

        return new GraphTokenProvider(
            httpClientFactory ?? new FakeHttpClientFactory(new HttpClient(new RecordingHttpMessageHandler())),
            config);
    }

    /// <summary>
    /// Creates a fake JWT whose exp claim is 1 hour in the future,
    /// so GraphTokenProvider.IsTokenExpiringSoon returns false.
    /// </summary>
    public static string CreateFakeJwt()
    {
        var header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"none\"}"));
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{{\"exp\":{exp}}}"));
        return $"{header}.{payload}.fakesig";
    }
}

internal sealed class TestDbContextFactory : IDbContextFactory<BerichtsportalDbContext>
{
    private readonly DbContextOptions<BerichtsportalDbContext> options;

    public TestDbContextFactory(string databaseName)
    {
        options = new DbContextOptionsBuilder<BerichtsportalDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
    }

    public BerichtsportalDbContext CreateDbContext() => new(options);

    public Task<BerichtsportalDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CreateDbContext());
    }
}

internal sealed class FakeScopeFactory : IServiceScopeFactory
{
    public IServiceScope CreateScope() => throw new NotSupportedException("Not used in tests");
}

internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient client;

    public FakeHttpClientFactory(HttpClient client)
    {
        this.client = client;
    }

    public HttpClient CreateClient(string name) => client;
}

internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage response;

    public RecordingHttpMessageHandler(HttpStatusCode statusCode = HttpStatusCode.Accepted)
    {
        response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{}")
        };
    }

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;

        if (request.Content != null)
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

        return response;
    }
}
