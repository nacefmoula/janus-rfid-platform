namespace Janus.Domain.Organizations;

/// <summary>
/// Format des codes publics d'organisation et de borne : majuscules ASCII, chiffres et tirets, commençant par une lettre
/// ou un chiffre. Les mêmes règles sont imposées en base par des contraintes CHECK (CK_Organizations_Code, CK_Terminals_Code).
/// </summary>
public static class OrganizationCodes
{
    /// <summary>Longueur minimale d'un code d'organisation.</summary>
    public const int OrganizationCodeMinLength = 2;
    /// <summary>Longueur minimale d'un code de borne.</summary>
    public const int TerminalCodeMinLength = 3;

    /// <summary>Normalise un code saisi (espaces retirés, majuscules invariantes) ; null si vide.</summary>
    public static string? Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    /// <summary>Indique si un code d'organisation normalisé est valide.</summary>
    public static bool IsValidOrganizationCode(string? code) =>
        IsValid(code, OrganizationCodeMinLength, JanusSchemaLimits.OrganizationCodeMaxLength);

    /// <summary>Indique si un code de borne normalisé est valide.</summary>
    public static bool IsValidTerminalCode(string? code) =>
        IsValid(code, TerminalCodeMinLength, JanusSchemaLimits.TerminalCodeMaxLength);

    private static bool IsValid(string? code, int minLength, int maxLength)
    {
        if (code is null || code.Length < minLength || code.Length > maxLength)
            return false;
        if (!IsUpperAlphanumeric(code[0]))
            return false;
        foreach (var c in code)
        {
            if (!IsUpperAlphanumeric(c) && c != '-')
                return false;
        }
        return true;
    }

    private static bool IsUpperAlphanumeric(char c) => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9');
}
