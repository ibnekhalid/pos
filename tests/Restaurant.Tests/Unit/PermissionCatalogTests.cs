using System.Reflection;
using FluentAssertions;
using Restaurant.Contracts.Auth;

namespace Restaurant.Tests.Unit;

public class PermissionCatalogTests
{
    private static readonly string[] Declared = typeof(Permissions)
        .GetNestedTypes()
        .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
        .Where(f => f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    [Fact]
    public void All_lists_every_declared_permission_exactly_once()
    {
        Permissions.All.Should().OnlyHaveUniqueItems();
        Permissions.All.Should().BeEquivalentTo(Declared);
    }

    [Fact]
    public void Permission_names_are_lowercase_area_dot_action()
    {
        Permissions.All.Should().AllSatisfy(p => p.Should().MatchRegex("^[a-z_]+\\.[a-z_]+$"));
    }
}
