namespace Restaurant.Application.Common.Security;

/// <summary>
/// People sign in with an email address or a mobile number. Both are normalised the same way
/// everywhere, so "0300-1234567", "+92 300 1234567" and "923001234567" are one account.
/// </summary>
public static class LoginNames
{
    public static bool IsEmail(string login) => login.Contains('@');

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>E.164 for Pakistani numbers (+92...); other numbers keep their digits with a leading +.</summary>
    public static string NormalizePhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00"))
        {
            digits = digits[2..];
        }
        else if (digits.Length == 11 && digits.StartsWith('0'))
        {
            digits = "92" + digits[1..];
        }
        return "+" + digits;
    }

    public static string Normalize(string login) => IsEmail(login) ? NormalizeEmail(login) : NormalizePhone(login);
}
