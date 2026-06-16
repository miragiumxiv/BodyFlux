using System;
using System.Security.Cryptography;
using System.Text;

namespace BodyFlux.Network;

/// <summary>
/// Derives the opaque peer id that travels over the relay in place of a character name.
///
/// The id is a salted SHA-256 of the character name, salted with the group's Sync Key. Because
/// the salt is the shared key, every member of the same group derives matching ids for the same
/// player, while the relay — which only forwards the ids — never sees a real name.
///
/// Resolution back to a real name is only possible <b>locally</b>: a client hashes the names of
/// the characters it can already see (its ObjectTable) and matches them against received ids. A
/// peer who is out of range therefore stays anonymous (only its id is known) until they come into
/// view, which is exactly the privacy property we want.
/// </summary>
public static class PeerIdentity
{
    /// <summary>Salted SHA-256 of <paramref name="characterName"/>, lowercase hex. Stable for a given key.</summary>
    public static string Of(string characterName, string syncKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(syncKey + "\0" + characterName));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Short, display-friendly form of an id for peers we cannot resolve to a name.</summary>
    public static string Short(string id) => id.Length <= 8 ? id : id[..8];
}
