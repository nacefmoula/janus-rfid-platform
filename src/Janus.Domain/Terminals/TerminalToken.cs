using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Janus.Domain.Terminals;

/// <summary>
/// Jeton opaque d'une borne : « jt_ » + 32 octets aléatoires (CSPRNG) encodés en base64url sans remplissage.
/// Seule l'empreinte SHA-256 est stockée ; avec 256 bits d'entropie, une empreinte sans sel ne permet pas de retrouver le jeton.
/// Format contractuel (empreinte de référence figée par test) : ne jamais le modifier.
/// </summary>
public static class TerminalToken
{
    /// <summary>Préfixe des jetons émis par la plateforme (facilite la détection de fuite par les scanners de secrets).</summary>
    public const string Prefix = "jt_";
    /// <summary>Nombre d'octets aléatoires d'un jeton.</summary>
    public const int RandomBytes = 32;

    /// <summary>Génère un nouveau jeton (à remettre une seule fois à l'opérateur).</summary>
    public static string Generate() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomBytes));

    /// <summary>Empreinte SHA-256 du jeton (UTF-8), en hexadécimal majuscule (64 caractères).</summary>
    public static string ComputeHash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
