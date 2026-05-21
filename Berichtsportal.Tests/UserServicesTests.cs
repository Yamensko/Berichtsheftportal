using Berichtsportal.Models;
using Berichtsportal.Services;

namespace Berichtsportal.Tests;

public class UserServicesTests
{
    [Fact]
    public async Task GetCurrentUserAsync_ResolvesUserFromClaimsEmail()
    {
        var databaseName = Guid.NewGuid().ToString();
        var factory = new TestDbContextFactory(databaseName);

        await using (var context = factory.CreateDbContext())
        {
            var azubi = TestData.CreateAzubi();
            context.Users.Add(azubi);
            await context.SaveChangesAsync();
        }

        var service = new UserServices(factory);
        var user = await service.GetCurrentUserAsync(TestData.CreatePrincipal("azubi@pica.de"));

        Assert.NotNull(user);
        Assert.Equal("Auszubildender", user!.Role.RoleName);
    }

    [Fact]
    public async Task GetAzubisForAusbilderAsync_ReturnsOnlyAssignedAzubis()
    {
        var databaseName = Guid.NewGuid().ToString();
        var factory = new TestDbContextFactory(databaseName);
        var ausbilder = TestData.CreateAusbilder();
        var assigned = TestData.CreateAzubi(mentorId: ausbilder.UserId);
        var other = new User
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

        Assert.Single(result);
        Assert.Equal(assigned.UserId, result.Single().UserId);
    }
}

