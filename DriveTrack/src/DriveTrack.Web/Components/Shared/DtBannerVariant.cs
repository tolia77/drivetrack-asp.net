namespace DriveTrack.Web.Components.Shared;

/// <summary>
/// The three faces a <c>DtFailureBanner</c> can wear.
/// <para>
/// Three, and there is deliberately no fourth for information. NFR-23's vocabulary has no
/// informational state and the palette declares no hue for one, so a banner that merely told the
/// reader something would have nothing to be drawn in - which is the point: a sentence that is
/// neither a refusal, a rule the reader can satisfy, nor a confirmation belongs in the screen's
/// own prose rather than in a coloured box.
/// </para>
/// </summary>
public enum DtBannerVariant
{
    /// <summary>
    /// A refusal: forbidden, a record that is gone, a conflict, or a rule naming a field this
    /// screen does not render. The default, because that is what the banner exists for.
    /// </summary>
    Danger,

    /// <summary>A rule stopped the action and the reader can satisfy it: "a shift is already running".</summary>
    Warning,

    /// <summary>A confirmation after something that leaves the screen looking unchanged.</summary>
    Success,
}
