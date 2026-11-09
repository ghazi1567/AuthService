using AuthService.Web.Api;

namespace AuthService.Web.Tests;

/// <summary>ONB-04: how the roles a product ships with are read from pasted JSON. No API is involved.</summary>
public sealed class RoleTemplateTextTests
{
    [Fact]
    public void RolesAreReadWithTheirDescriptionAndGrantsAndOtherPropertiesAreIgnored()
    {
        var (roles, errors) = RoleTemplateText.Parse("""
            { "permissions": [ { "key": "campaign.view", "label": "View" } ],
              "roles": [
                { "name": "Marketer", "description": "Builds and sends campaigns",
                  "permissions": [ { "key": "campaign.view", "level": "ReadOnly" }, { "Key": "campaign.send", "Level": "CreateRead" } ] },
                { "name": "Agent", "description": "  ", "permissions": [] }
              ] }
            """);

        errors.ShouldBeEmpty();
        roles.ShouldNotBeNull();
        roles.Count.ShouldBe(2);
        roles[0].Name.ShouldBe("Marketer");
        roles[0].Description.ShouldBe("Builds and sends campaigns");
        roles[0].Permissions.ShouldBe([new ProductRoleGrant("campaign.view", "ReadOnly"), new ProductRoleGrant("campaign.send", "CreateRead")]);
        roles[1].Description.ShouldBeNull();
        roles[1].Permissions.ShouldBeEmpty();
    }

    [Fact]
    public void AnEmptyListIsAListThatMeansNoRoles()
    {
        var (roles, errors) = RoleTemplateText.Parse("""{ "roles": [] }""");

        errors.ShouldBeEmpty();
        roles.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("campaign.view | View campaigns")]
    [InlineData("""[{"key":"a.b","label":"A"}]""")]
    [InlineData("""{ "permissions": [] }""")]
    [InlineData("""{ "roles": "none" }""")]
    public void AnythingWithNoRolesListIsNullAndNotAnError(string? text)
    {
        var (roles, errors) = RoleTemplateText.Parse(text);

        roles.ShouldBeNull();
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void AnEntryWithoutANameOrAGrantWithoutAKeyOrLevelIsNamedByItsNumberAndTheRestAreStillRead()
    {
        var (roles, errors) = RoleTemplateText.Parse("""
            { "roles": [
                { "name": "Good", "permissions": [ { "key": "a.b", "level": "Full" }, { "key": "c.d" }, { "level": "Full" }, "nope" ] },
                { "description": "no name" },
                "also no name"
            ] }
            """);

        roles.ShouldNotBeNull().Count.ShouldBe(1);
        roles[0].Permissions.ShouldBe([new ProductRoleGrant("a.b", "Full")]);
        errors.ShouldBe([
            "Role 1, permission 2: write a key and a level.",
            "Role 1, permission 3: write a key and a level.",
            "Role 1, permission 4: write a key and a level.",
            "Role 2: write a name.",
            "Role 3: write a name.",
        ]);
    }

    [Fact]
    public void BrokenJsonSaysSoWithoutTechnicalDetail()
    {
        var (roles, errors) = RoleTemplateText.Parse("{\n  \"roles\": [\n    { \"name\": \n");

        roles.ShouldBeNull();
        errors.ShouldHaveSingleItem().ShouldStartWith("That isn't valid JSON. Check it near line ");
    }

    [Fact]
    public void RolesWrittenAsJsonReadBackTheSame()
    {
        var original = new List<ProductRoleTemplate>
        {
            new(Guid.NewGuid(), "Marketer", "Builds campaigns", [new ProductRoleGrant("campaign.view", "ReadOnly")]),
            new(Guid.NewGuid(), "Agent", null, []),
        };

        var (roles, errors) = RoleTemplateText.Parse(RoleTemplateText.ToJson(original));

        errors.ShouldBeEmpty();
        roles.ShouldNotBeNull().Select(role => (role.Name, role.Description, role.Permissions.Count)).ToList()
            .ShouldBe([("Marketer", "Builds campaigns", 1), ("Agent", null, 0)]);
    }
}
