using Berichtsportal.Data;
using Berichtsportal.Models;
using Berichtsportal.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Security.Claims;

namespace Berichtsportal.Tests;

// Hilfsklasse mit Testdaten und Factory-Methoden fuer alle Tests
internal static class TestData
{
    /// Erstellt einen neuen InMemory-DbContext (jeder Test bekommt eine eigene DB)
    public static BerichtsportalDbContext CreateContext(string? name = null)
    {
        var options = new DbContextOptionsBuilder<BerichtsportalDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .Options;

        return new BerichtsportalDbContext(options);
    }

    // Vordefinierte Rollen fuer Tests
    public static Role AzubiRole() => new() { RoleId = 1, RoleName = "Auszubildender" };

    public static Role AusbilderRole() => new() { RoleId = 2, RoleName = "Ausbilder" };

    /// Erstellt einen Test-Ausbilder mit Standardwerten
    public static User CreateAusbilder(Guid? id = null) => new()
    {
        UserId = id ?? Guid.NewGuid(),
        Email = "ausbilder@example.com",
        FirstName = "Ada",
        LastName = "Ausbilder",
        RoleId = 2,
        Role = AusbilderRole()
    };

    /// Erstellt einen Test-Azubi mit Ausbildungszeitraum und optionalem Mentor
    public static User CreateAzubi(Guid? id = null, Guid? mentorId = null) => new()
    {
        UserId = id ?? Guid.NewGuid(),
        Email = "azubi@example.com",
        FirstName = "Max",
        LastName = "Azubi",
        RoleId = 1,
        Role = AzubiRole(),
        MentorId = mentorId,
        AusbildungBeginn = new DateTime(2026, 1, 1),
        AusbildungEnde = new DateTime(2028, 12, 31)
    };

    /// Erstellt ein ClaimsPrincipal (simuliert einen angemeldeten Benutzer)
    /// Optional mit Graph-Token und Azure Object ID fuer Mail-Tests
    public static ClaimsPrincipal CreatePrincipal(string email = "azubi@example.com", string? graphToken = null, string? objectId = null)
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

    /// Erstellt den SubmitNotificationService (BackgroundService fuer verzögerte Benachrichtigungen)
    public static SubmitNotificationService CreateNotificationService()
    {
        var scopeFactory = new FakeScopeFactory();
        return new SubmitNotificationService(scopeFactory);
    }

    /// Erstellt einen GraphTokenProvider mit Fake-Konfiguration fuer Tests
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

    /// Erstellt ein Fake-JWT mit exp-Claim 1 Stunde in der Zukunft,
    /// damit IsTokenExpiringSoon false zurueckgibt (Token gilt noch)
    public static string CreateFakeJwt()
    {
        var header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"none\"}"));
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{{\"exp\":{exp}}}"));
        return $"{header}.{payload}.fakesig";
    }
}

/// Fake-DbContextFactory: Gibt immer einen InMemory-Context zurueck
/// Noetig weil UserServices IDbContextFactory statt DbContext direkt nutzt
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

/// Fake-ScopeFactory: Wird vom NotificationService benoetigt aber in Tests nicht aufgerufen
internal sealed class FakeScopeFactory : IServiceScopeFactory
{
    public IServiceScope CreateScope() => throw new NotSupportedException("Not used in tests");
}

/// Fake-HttpClientFactory: Gibt immer denselben HttpClient zurueck
/// Damit koennen wir den RecordingHttpMessageHandler injizieren
internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient client;

    public FakeHttpClientFactory(HttpClient client)
    {
        this.client = client;
    }

    public HttpClient CreateClient(string name) => client;
}

/// Aufzeichnender HTTP-Handler: Speichert den letzten Request und Body
/// Damit koennen wir pruefen ob der richtige API-Aufruf gesendet wurde
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
        // Request und Body aufzeichnen fuer spaetere Assertions
        LastRequest = request;

        if (request.Content != null)
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

        return response;
    }
}
