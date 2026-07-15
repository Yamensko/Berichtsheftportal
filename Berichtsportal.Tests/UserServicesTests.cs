using Berichtsportal.Models;
using Berichtsportal.Services;

namespace Berichtsportal.Tests;

/// Tests fuer den UserService (Benutzer-Lookup und Azubi-Zuordnung)
public class UserServicesTests
{
    /// Prueft ob ein Benutzer anhand seiner E-Mail aus den Claims gefunden wird
    [Fact]
    public async Task GetCurrentUserAsync_ResolvesUserFromClaimsEmail()
    {
        var databaseName = Guid.NewGuid().ToString();
        var factory = new TestDbContextFactory(databaseName);

        // Azubi in der InMemory-DB anlegen
        await using (var context = factory.CreateDbContext())
        {
            var azubi = TestData.CreateAzubi();
            context.Users.Add(azubi);
            await context.SaveChangesAsync();
        }

        // Service erstellen und Benutzer per ClaimsPrincipal suchen
        var service = new UserServices(factory);
        var user = await service.GetCurrentUserAsync(TestData.CreatePrincipal("azubi@pica.de"));

        // Benutzer gefunden und hat die richtige Rolle
        Assert.NotNull(user);
        Assert.Equal("Auszubildender", user!.Role.RoleName);
    }

    /// Prueft ob nur die dem Ausbilder zugewiesenen Azubis zurueckgegeben werden
    /// (nicht alle Azubis in der DB)
    [Fact]
    public async Task GetAzubisForAusbilderAsync_ReturnsOnlyAssignedAzubis()
    {
        var databaseName = Guid.NewGuid().ToString();
        var factory = new TestDbContextFactory(databaseName);
        var ausbilder = TestData.CreateAusbilder();
        var assigned = TestData.CreateAzubi(mentorId: ausbilder.UserId);  // Zugewiesener Azubi
        var other = new User                                               // Azubi eines anderen Ausbilders
        {
            UserId = Guid.NewGuid(),
            Email = "anderer.azubi@pica.de",
            FirstName = "Andere",
            LastName = "Person",
            RoleId = 1,
            Role = assigned.Role,
            MentorId = Guid.NewGuid(),
            AusbildungBeginn = new DateTime(2026, 1, 1),
            AusbildungEnde = new DateTime(2028, 12, 31)
        };

        await using (var context = factory.CreateDbContext())
        {
            context.Users.AddRange(ausbilder, assigned, other);
            await context.SaveChangesAsync();
        }

        var service = new UserServices(factory);
        var result = await service.GetAzubisForAusbilderAsync(ausbilder.UserId);

        // Nur 1 Azubi zurueckgegeben: der zugewiesene, nicht der andere
        Assert.Single(result);
        Assert.Equal(assigned.UserId, result.Single().UserId);
    }
}
