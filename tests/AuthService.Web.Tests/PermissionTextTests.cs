using AuthService.Web.Api;

namespace AuthService.Web.Tests;

/// <summary>ONB-01: how a pasted permission list is read, as lines or as the JSON a product's team keeps. No API is involved.</summary>
public sealed class PermissionTextTests
{
    [Fact]
    public void LinesAreKeyLabelAndAnOptionalDescriptionAndBlankLinesAreSkipped()
    {
        var (permissions, errors) = PermissionText.Parse("ticket.reply | Answer tickets | Reply to a customer\n\n  ticket.close|Close tickets  \r\n");

        errors.ShouldBeEmpty();
        permissions.ShouldBe([new PermissionToAdd("ticket.reply", "Answer tickets", "Reply to a customer"), new PermissionToAdd("ticket.close", "Close tickets", null)]);
    }

    [Theory]
    [InlineData("just-a-key")]
    [InlineData("| Label only")]
    [InlineData("key only |")]
    public void ALineWithoutAKeyAndALabelIsNamedByItsNumber(string line)
    {
        var (permissions, errors) = PermissionText.Parse($"ticket.reply | Answer tickets\n{line}");

        permissions.Count.ShouldBe(1);
        errors.ShouldBe(["Line 2: write a key and a label separated by a | sign."]);
    }

    [Fact]
    public void AnObjectWithAPermissionsListIsRead()
    {
        var (permissions, errors) = PermissionText.Parse("""
            { "permissions": [
                { "key": "contact.view", "label": "View contacts", "description": "See contacts" },
                { "Key": "user.manage", "Label": "Manage users", "description": "  " }
            ], "roles": [ { "name": "Admin" } ] }
            """);

        errors.ShouldBeEmpty();
        permissions.ShouldBe([new PermissionToAdd("contact.view", "View contacts", "See contacts"), new PermissionToAdd("user.manage", "Manage users", null)]);
    }

    [Fact]
    public void AListOnItsOwnIsRead()
    {
        var (permissions, errors) = PermissionText.Parse("""[{"key":"a.b","label":"A B"}]""");

        errors.ShouldBeEmpty();
        permissions.ShouldBe([new PermissionToAdd("a.b", "A B", null)]);
    }

    [Fact]
    public void AnEntryWithoutAKeyOrALabelIsNamedByItsNumberAndTheRestAreStillRead()
    {
        var (permissions, errors) = PermissionText.Parse("""[{"key":"a.b","label":"A B"},{"key":"c.d"},"nope",{"label":"x"}]""");

        permissions.Count.ShouldBe(1);
        errors.ShouldBe(["Permission 2: write a key and a label.", "Permission 3: write a key and a label.", "Permission 4: write a key and a label."]);
    }

    [Fact]
    public void JsonThatIsBrokenSaysSoWithoutTechnicalDetail()
    {
        var (permissions, errors) = PermissionText.Parse("{\n  \"permissions\": [\n    { \"key\": \n");

        permissions.ShouldBeEmpty();
        errors.ShouldHaveSingleItem().ShouldStartWith("That isn't valid JSON. Check it near line ");
    }

    [Theory]
    [InlineData("""{"roles":[]}""")]
    [InlineData("""{"permissions":"nope"}""")]
    [InlineData("[")]
    public void JsonWithoutAPermissionsListIsExplained(string text)
    {
        var (permissions, errors) = PermissionText.Parse(text);

        permissions.ShouldBeEmpty();
        errors.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n  ")]
    public void NothingTypedIsNothingAndNotAnError(string? text)
    {
        var (permissions, errors) = PermissionText.Parse(text);

        permissions.ShouldBeEmpty();
        errors.ShouldBeEmpty();
    }
}
