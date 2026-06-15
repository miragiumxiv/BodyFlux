using System.Numerics;
using Newtonsoft.Json.Linq;

namespace BodyFlux.Morph;

/// <summary>
/// Pure static helpers for reading and writing bone transforms in Customize+ profile JSON.
/// All methods are side-effect free except for <see cref="SetBoneTransform"/>, which
/// mutates only the <paramref name="bones"/> node that was explicitly supplied.
/// </summary>
internal static class BoneJsonHelper
{
    // ── Primitives ────────────────────────────────────────────────────────────

    public static JObject Vec3Json(Vector3 v) =>
        new() { ["X"] = v.X, ["Y"] = v.Y, ["Z"] = v.Z };

    private static Vector3 ReadVec3(JObject? node, float defaultVal) =>
        node == null ? new Vector3(defaultVal) :
        new Vector3(
            node["X"]?.Value<float>() ?? defaultVal,
            node["Y"]?.Value<float>() ?? defaultVal,
            node["Z"]?.Value<float>() ?? defaultVal);

    // ── Bone read ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the Translation, Rotation and Scale channels from <paramref name="bones"/>[<paramref name="boneName"/>].
    /// Missing bones or channels fall back to (0,0,0) for T/R and (1,1,1) for S.
    /// </summary>
    public static (Vector3 translation, Vector3 rotation, Vector3 scale)
        ReadBoneTransform(JObject bones, string boneName)
    {
        if (bones[boneName] is not JObject bone)
            return (Vector3.Zero, Vector3.Zero, Vector3.One);

        return (
            ReadVec3(bone["Translation"] as JObject, 0f),
            ReadVec3(bone["Rotation"]    as JObject, 0f),
            ReadVec3(bone["Scaling"]     as JObject, 1f));
    }

    // ── Bone write ────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes Translation, Rotation and Scale into <paramref name="bones"/>[<paramref name="boneName"/>].
    /// Creates the entry with default C+ metadata when absent; on an existing entry only
    /// the three transform channels are touched — ChildScaling and propagation flags are preserved.
    /// </summary>
    public static void SetBoneTransform(JObject bones, string boneName,
        Vector3 translation, Vector3 rotation, Vector3 scale)
    {
        if (bones[boneName] is not JObject bone)
        {
            bones[boneName] = new JObject
            {
                ["Translation"]           = Vec3Json(translation),
                ["Rotation"]              = Vec3Json(rotation),
                ["Scaling"]               = Vec3Json(scale),
                ["ChildScaling"]          = Vec3Json(Vector3.One),
                ["ChildScaleIndependent"] = false,
                ["PropagateTranslation"]  = false,
                ["PropagateRotation"]     = false,
                ["PropagateScale"]        = false
            };
            return;
        }

        WriteChannel(bone, "Translation", translation, 0f);
        WriteChannel(bone, "Rotation",    rotation,    0f);
        WriteChannel(bone, "Scaling",     scale,       1f);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void WriteChannel(JObject bone, string key, Vector3 val, float defaultVal)
    {
        if (bone[key] is JObject ch)
        {
            ch["X"] = val.X;
            ch["Y"] = val.Y;
            ch["Z"] = val.Z;
        }
        else if (val != new Vector3(defaultVal))
        {
            // Channel absent on an existing bone — add it only when it carries a non-default value
            bone[key] = Vec3Json(val);
        }
    }
}
