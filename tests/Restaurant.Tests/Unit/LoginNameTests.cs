using FluentAssertions;
using Restaurant.Application.Common.Security;

namespace Restaurant.Tests.Unit;

public class LoginNameTests
{
    [Theory]
    [InlineData("0300-1234567", "+923001234567")]
    [InlineData("+92 300 1234567", "+923001234567")]
    [InlineData("923001234567", "+923001234567")]
    [InlineData("0092 300 1234567", "+923001234567")]
    [InlineData("+971 50 123 4567", "+971501234567")]
    public void Mobile_numbers_normalise_to_one_form(string input, string expected) =>
        LoginNames.Normalize(input).Should().Be(expected);

    [Fact]
    public void Emails_are_case_insensitive() =>
        LoginNames.Normalize("  Ali@Example.PK ").Should().Be("ali@example.pk");

    [Fact]
    public void Invite_codes_ignore_case_spaces_and_dashes() =>
        Secrets.HashInviteCode("k7p3 qx9m").Should().Be(Secrets.HashInviteCode("K7P3-QX9M"));
}
