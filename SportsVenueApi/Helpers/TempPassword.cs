using System.Security.Cryptography;

namespace SportsVenueApi.Helpers;

/// <summary>
/// Generates the one-off password an admin hands to a locked-out user.
///
/// The server generates it rather than letting the admin choose. An admin picking one under
/// time pressure reaches for something they already use, or something weak enough to say
/// twice over a bad phone line — and it is the password to somebody else's account.
/// </summary>
public static class TempPassword
{
    /// <summary>
    /// Deliberately excludes characters that are indistinguishable when spoken or read from a
    /// screen: O/0, I/l/1, S/5, B/8. This password's entire life is being read down a phone
    /// to a clerk who then types it, so a character that survives that trip matters more than
    /// squeezing in the last few bits of entropy.
    /// </summary>
    private const string Alphabet = "ABCDEFGHJKMNPQRTUVWXYZabcdefghijkmnpqrtuvwxyz234679";

    /// <summary>
    /// 14 characters from a 50-character alphabet is ~79 bits — far beyond anything that
    /// matters for a credential that should be replaced within the hour, and comfortably past
    /// the 8-character minimum the rest of the system enforces.
    /// </summary>
    public static string Generate(int length = 14)
    {
        // RandomNumberGenerator, not Random: this is a credential. GetInt32 is rejection-
        // sampled internally, so the distribution stays uniform rather than skewing toward
        // the start of the alphabet the way a naive modulo would.
        return string.Create(length, Alphabet, (span, alphabet) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }
}
