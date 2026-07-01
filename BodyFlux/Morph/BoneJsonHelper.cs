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

    /// <summary>
    /// Returns a deep clone of the bone's full JSON entry from whichever source profile defines it
    /// (destination takes priority, then origin), or null if neither has it. Used as the working-
    /// profile template so Customize+ metadata (PropagateScale/Rotation/Translation,
    /// ChildScalingIndependent, ChildScaling, …) survives the morph untouched.
    /// </summary>
    public static JObject? CloneBoneTemplate(JObject originBones, JObject destBones, string boneName)
    {
        if (destBones[boneName] is JObject d) return (JObject)d.DeepClone();
        if (originBones[boneName] is JObject o) return (JObject)o.DeepClone();
        return null;
    }

    /// <summary>
    /// True when the bone's source entry is a Customize+ "linked" scale parent (PropagateScale set),
    /// i.e. its scale is meant to propagate to child bones. Destination takes precedence over origin,
    /// matching <see cref="CloneBoneTemplate"/> so the link state follows the morph target.
    /// </summary>
    public static bool IsLinkedScale(JObject originBones, JObject destBones, string boneName)
    {
        if (destBones[boneName]   is JObject d) return d["PropagateScale"]?.Value<bool>() == true;
        if (originBones[boneName] is JObject o) return o["PropagateScale"]?.Value<bool>() == true;
        return false;
    }

    /// <summary>
    /// Builds a virtual destination "Bones" node for <see cref="MorphTargetMode.TemplateOverlay"/>: a
    /// deep clone of <paramref name="originBones"/> with every bone present in
    /// <paramref name="overlayBones"/> replaced by that bone's entry. Bones absent from the overlay
    /// stay identical to the origin, so <see cref="MorphController"/>'s ordinary origin/destination
    /// interpolation leaves them static — only the overlaid bones actually animate.
    /// </summary>
    public static JObject BuildOverlayDestination(JObject originBones, JObject overlayBones)
    {
        var merged = (JObject)originBones.DeepClone();
        foreach (var prop in overlayBones.Properties())
            if (prop.Value is JObject overlayBone)
                merged[prop.Name] = (JObject)overlayBone.DeepClone();
        return merged;
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
                ["ChildScaleIndependent"] = false, // IPC schema name (see SetLinkedChildScaling)
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

    /// <summary>
    /// Expresses a Customize+ "linked" parent's child-scale magnitude explicitly so it survives the
    /// SetTemporaryProfile round-trip and the per-frame morph.
    ///
    /// In linked mode (<c>ChildScalingIndependent == false</c>, <c>PropagateScale == true</c>) C+
    /// does NOT serialise ChildScaling (<c>ShouldSerializeChildScaling() =&gt; ChildScalingIndependent</c>)
    /// and derives the child delta from the bone's *live* animated scale. That live derivation
    /// collapses to a no-op under BodyFlux's per-frame temporary-profile applies, so the parent
    /// scales but its children (e.g. toes under a foot) do not follow.
    ///
    /// Switching the bone to an *independent* child scale makes C+ compute the child delta as
    /// <c>(initialScale × ChildScaling) / initialScale</c> = <c>ChildScaling</c> — exact and
    /// independent of the live pose — reproducing the linked look. PropagateScale is set here so
    /// C+'s propagation gate stays open.
    ///
    /// <paramref name="scale"/> is the EXTRA factor applied on top of each child's own scale, not the
    /// parent's own scale. Callers ramp it from 1 (origin: children already drawn at their own scale)
    /// to the destination foot scale; using the parent's own scale instead would double up with a
    /// child that still has a non-1 own scale early in the morph and cause a start-of-morph pop.
    /// </summary>
    public static void SetLinkedChildScaling(JObject bones, string boneName, Vector3 scale)
    {
        if (bones[boneName] is not JObject bone) return;

        // PropagateScale MUST be set true here, not merely inherited from the cloned template: C+'s
        // apply gate is `doPropagate = PropagateTranslation || PropagateRotation || PropagateScale`,
        // and a bone that fails it never propagates to children at all. GetProfile does not reliably
        // carry PropagateScale on the cloned (destination) entry, so we assert it explicitly.
        bone["PropagateScale"] = true;

        // NOTE: the field is "ChildScaleIndependent" (no "-ing", "Scale" not "Scaling"). That is the
        // exact name Customize+'s IPC schema (IPCBoneTransform) deserialises; the differently-spelled
        // "ChildScalingIndependent" used inside C+'s own profile format is silently ignored over IPC.
        bone["ChildScaleIndependent"] = true;
        bone["ChildScaling"]          = Vec3Json(scale); // written unconditionally: an independent
                                                          // bone must carry a valid ChildScaling.
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
