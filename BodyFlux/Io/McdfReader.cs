using System;
using System.IO;
using System.Text;
using K4os.Compression.LZ4.Legacy;
using Newtonsoft.Json.Linq;

namespace BodyFlux.Io;

/// <summary>
/// Reads Mare/Lightless MCDF character files and extracts the embedded
/// Customize+ profile (the body scaling) so it can be used as a morph origin.
///
/// This exists because Customize+ exposes no IPC to read the *temporary* profile
/// applied to a Brio clone, so the clone's original scaling is otherwise unreadable.
/// The MCDF is the source of that scaling, so we read it directly from the file.
///
/// File layout (the whole file is LZ4-legacy compressed; after decompression):
///   "MCDF"            – 4-byte ASCII magic
///   version           – 1 byte
///   headerLength      – int32 little-endian
///   headerJson        – UTF-8 JSON object, contains "CustomizePlusData": base64(profileJson)
///   ...file blobs...  – textures / models (ignored here)
/// </summary>
public static class McdfReader
{
    private const string Magic = "MCDF";

    /// <param name="Ok">True when a Customize+ profile was successfully extracted.</param>
    /// <param name="CustomizePlusJson">The decoded Customize+ profile JSON (origin), or null on failure.</param>
    /// <param name="Error">Human-readable failure reason, or null on success.</param>
    public readonly record struct Result(bool Ok, string? CustomizePlusJson, string? Error);

    /// <summary>
    /// Decompresses the MCDF, parses its header, and returns the embedded Customize+ profile JSON.
    /// Never throws — all failures are reported via <see cref="Result.Error"/>.
    /// </summary>
    public static Result TryReadCustomizePlusProfile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Result(false, null, "File does not exist.");

            using var file = File.OpenRead(path);
            using var lz4  = LZ4Legacy.Decode(file);
            // Only the header sits near the start; BinaryReader streams it without
            // decompressing the (potentially huge) trailing texture/model blobs.
            using var reader = new BinaryReader(lz4, Encoding.UTF8, leaveOpen: false);

            var magic = new string(reader.ReadChars(4));
            if (magic != Magic)
                return new Result(false, null, $"Not an MCDF file (magic was '{magic}').");

            reader.ReadByte();                  // format version – not needed
            int headerLen = reader.ReadInt32(); // header JSON byte length
            if (headerLen <= 0 || headerLen > 16 * 1024 * 1024)
                return new Result(false, null, $"Implausible header length ({headerLen}).");

            var headerBytes = reader.ReadBytes(headerLen);
            if (headerBytes.Length != headerLen)
                return new Result(false, null, "Truncated MCDF header.");

            var headerJson = Encoding.UTF8.GetString(headerBytes);
            var header     = JObject.Parse(headerJson);

            var cpData = header["CustomizePlusData"]?.Value<string>();
            if (string.IsNullOrEmpty(cpData))
                return new Result(false, null, "This MCDF contains no Customize+ data.");

            var profileJson = Encoding.UTF8.GetString(Convert.FromBase64String(cpData));
            if (string.IsNullOrWhiteSpace(profileJson) || !profileJson.Contains("\"Bones\""))
                return new Result(false, null, "Embedded Customize+ data is empty or malformed.");

            return new Result(true, profileJson, null);
        }
        catch (Exception ex)
        {
            return new Result(false, null, ex.Message);
        }
    }
}
