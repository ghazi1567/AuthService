using System.Security.Claims;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;

namespace AuthService.IntegrationTests.Tok03;

/// <summary>
/// The permission check the Auth Service's own tenant endpoints use: a permission is held at a level, and the level is compared,
/// never a role name. No database or server is needed to prove it.
/// </summary>
public sealed class PermissionRequirementTests
{
    [Theory]
    [InlineData("roles.manage:Full", "roles.manage", AccessLevel.Full, true)]
    [InlineData("roles.manage:Full", "roles.manage", AccessLevel.CreateRead, true)]
    [InlineData("roles.manage:Full", "roles.manage", AccessLevel.ReadOnly, true)]
    [InlineData("roles.manage:CreateRead", "roles.manage", AccessLevel.Full, false)]
    [InlineData("roles.manage:CreateRead", "roles.manage", AccessLevel.CreateRead, true)]
    [InlineData("roles.manage:ReadOnly", "roles.manage", AccessLevel.CreateRead, false)]
    [InlineData("roles.manage:ReadOnly", "roles.manage", AccessLevel.ReadOnly, true)]
    [InlineData("roles.manage:None", "roles.manage", AccessLevel.ReadOnly, false)]
    [InlineData("roles.read:Full", "roles.manage", AccessLevel.ReadOnly, false)]
    [InlineData("roles.manage.extra:Full", "roles.manage", AccessLevel.ReadOnly, false)]
    [InlineData("Roles.Manage:Full", "roles.manage", AccessLevel.ReadOnly, false)]
    [InlineData("roles.manage:full", "roles.manage", AccessLevel.ReadOnly, false)]
    [InlineData("roles.manage:Bogus", "roles.manage", AccessLevel.ReadOnly, false)]
    [InlineData("roles.manage:7", "roles.manage", AccessLevel.ReadOnly, false)]
    [InlineData("roles.manage", "roles.manage", AccessLevel.ReadOnly, false)]
    [InlineData(":Full", "roles.manage", AccessLevel.ReadOnly, false)]
    public async Task APermissionIsHeldOnlyAtOrAboveTheRequiredLevel(string claim, string key, AccessLevel minimum, bool expected)
    {
        var succeeded = await EvaluateAsync([claim], key, minimum);

        succeeded.ShouldBe(expected);
    }

    [Fact]
    public async Task TheHighestMatchingClaimWinsAndOtherPermissionsDoNotMatter()
    {
        (await EvaluateAsync(["members.read:Full", "roles.manage:ReadOnly", "roles.manage:Full"], "roles.manage", AccessLevel.Full)).ShouldBeTrue();
        (await EvaluateAsync(["members.read:Full", "roles.manage:ReadOnly"], "roles.manage", AccessLevel.Full)).ShouldBeFalse();
    }

    [Fact]
    public async Task ATokenWithNoPermissionsHoldsNone()
    {
        (await EvaluateAsync([], "roles.manage", AccessLevel.ReadOnly)).ShouldBeFalse();
    }

    private static async Task<bool> EvaluateAsync(string[] permissionClaims, string key, AccessLevel minimum)
    {
        var requirement = new PermissionRequirement(key, minimum);
        var identity = new ClaimsIdentity(permissionClaims.Select(value => new Claim(ProductClaims.Permissions, value)), "test");
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(identity), resource: null);

        await new PermissionHandler().HandleAsync(context);

        return context.HasSucceeded;
    }
}
