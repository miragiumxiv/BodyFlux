using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BodyFlux.Network;

/// <summary>
/// Prepares Customize+ profile JSON for transmission over the relay.
///
/// Two transforms, applied at the network boundary only (local Customize+ still receives the full
/// profile, so behaviour there is unchanged):
///   1. <see cref="Minimize"/> strips the profile to {ID, Name, Bones}. Peers only ever read the
///      Bones, so every other field is dead weight — and a full C+ profile can carry a user-chosen
///      profile name and character associations that would otherwise leak identity through the relay.
///   2. <see cref="Pack"/> DEFLATE-compresses and base64-encodes the result. Bone JSON is highly
///      repetitive (the same keys per bone), so this typically shrinks the per-frame payload several
///      fold. Only the profile field is packed; the envelope (sender id, type) stays readable text
///      so the relay can keep forwarding opaquely — it never decompresses anything.
/// </summary>
public static class ProfileWire
{
    // Constant header so the receiver (and C+) always gets a structurally valid profile without
    // echoing the sender's real profile name / id.
    private const string AnonId   = "00000000-0000-0000-0000-000000000000";
    private const string AnonName = "BodyFlux";

    /// <summary>Reduce a full profile to {ID, Name, Bones}, dropping all other (identifying) fields.</summary>
    public static string Minimize(string profileJson)
    {
        try
        {
            var bones = JObject.Parse(profileJson)["Bones"] as JObject ?? new JObject();
            return new JObject
            {
                ["ID"]    = AnonId,
                ["Name"]  = AnonName,
                ["Bones"] = bones,
            }.ToString(Formatting.None);
        }
        catch { return profileJson; } // malformed — send as-is rather than dropping the frame
    }

    /// <summary>base64( DEFLATE( utf8(json) ) ) — the on-wire form of a profile field.</summary>
    public static string Pack(string json)
    {
        var raw = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(raw, 0, raw.Length);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Inverse of <see cref="Pack"/>.</summary>
    public static string Unpack(string packed)
    {
        var bytes = Convert.FromBase64String(packed);
        using var ms    = new MemoryStream(bytes);
        using var ds    = new DeflateStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        ds.CopyTo(outMs);
        return Encoding.UTF8.GetString(outMs.ToArray());
    }
}
