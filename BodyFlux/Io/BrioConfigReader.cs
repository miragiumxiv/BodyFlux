using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace BodyFlux.Io;

/// <summary>
/// Reads Brio's plugin configuration to recover the last folder the user imported
/// an MCDF from. Used purely as a convenience to pre-fill the file picker — it is
/// never required and every failure mode falls back to an empty start path.
/// </summary>
public static class BrioConfigReader
{
    /// <summary>
    /// Returns Brio's last-used MCDF import folder, or <c>null</c> if it can't be
    /// determined (Brio not installed, config missing, folder no longer exists, …).
    /// </summary>
    /// <param name="pluginConfigsDir">
    /// The shared <c>pluginConfigs</c> directory (our own config file's directory),
    /// alongside which <c>Brio.json</c> lives.
    /// </param>
    public static string? GetLastMcdfFolder(string? pluginConfigsDir)
    {
        try
        {
            if (string.IsNullOrEmpty(pluginConfigsDir))
                return null;

            var brioConfig = Path.Combine(pluginConfigsDir, "Brio.json");
            if (!File.Exists(brioConfig))
                return null;

            var json   = JObject.Parse(File.ReadAllText(brioConfig));
            var folder = json["LastMCDFPath"]?.Value<string>();

            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : null;
        }
        catch
        {
            return null;
        }
    }
}
