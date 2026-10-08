namespace Who.Domain.Accounts;

public static class AccountRules
{
    public const int DisplayNameMaxLength = 120;

    // Flutter Username.normalize: Unicode whitespace/BOM trim, one @, ASCII only.
    public static string? NormalizeUsername(string? input)
    {
        if (input is null) return null;
        var value = input.Trim().Trim('\uFEFF').Trim();
        while (value.Length > 0 && (char.IsWhiteSpace(value[0]) || value[0] == '\uFEFF')) value = value[1..];
        while (value.Length > 0 && (char.IsWhiteSpace(value[^1]) || value[^1] == '\uFEFF')) value = value[..^1];
        if (value.StartsWith('@')) value = value[1..];
        if (value.Length == 0 || value.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            return null;
        return value.ToLowerInvariant(); // No invented length cap.
    }

    public static string? BirthDateError(DateOnly birthDate, DateOnly today)
    {
        if (birthDate > today || birthDate < today.AddYears(-130)) return "BIRTH_DATE_INVALID";
        var age = today.Year - birthDate.Year;
        if (today.Month < birthDate.Month || (today.Month == birthDate.Month && today.Day < birthDate.Day)) age--;
        return age < 13 ? "AGE_NOT_ELIGIBLE" : null;
    }
}
