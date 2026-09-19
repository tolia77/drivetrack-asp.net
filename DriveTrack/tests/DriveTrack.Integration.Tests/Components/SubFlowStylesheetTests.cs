using System.Text.RegularExpressions;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// The two rules the dialog-hosted panels ask of themselves, read out of their stylesheets.
/// <para>
/// Both are invisible to every other kind of test here. The rendered markup carries no evidence of
/// either — a class attribute is present whether or not a rule paints it — and
/// <c>DesignTokenTests</c> reads these files only for literal palette values and for token names
/// that do not exist, never for which declaration a selector carries. Delete either rule and the
/// suite stays green while a driver's thumb loses its target and a long account name pushes a
/// dialog sideways.
/// </para>
/// <para>
/// Read as source rather than rendered, because a scoped stylesheet is not in the response: it is
/// in the bundle the shell links, and no test in this project reads that bundle. The shape follows
/// <c>AnonymousScreenTests</c>, which pins its own panel's width bound the same way.
/// </para>
/// </summary>
public class SubFlowStylesheetTests
{
    /// <summary>
    /// Every button in the timeline panel clears the phone hit floor, and only on a phone.
    /// </summary>
    /// <remarks>
    /// Gated, unlike <c>ProofCapture</c>, which floors its controls at every width. That panel is a
    /// driver's capture flow and a driver is holding a phone; this one is opened from the dispatch
    /// board as well as from own-deliveries, so an ungated floor would grow deliberately small
    /// buttons on a desktop. The gate is the claim as much as the floor is.
    /// </remarks>
    [Fact]
    public void The_timeline_panels_buttons_clear_the_thumb_floor_on_a_phone()
    {
        var css = WithoutComments(SharedMarkup.ReadComponent("Pages", "DeliveryTimeline.razor.css"));

        var query = PhoneQuery.Match(css);

        Assert.True(query.Success, "The timeline stylesheet states no phone breakpoint.");

        // Both blocks, because the note action and the transitions are separate groups and a floor
        // on one of them is the other one missed.
        foreach (var block in new[] { "dt-timeline-compose", "dt-timeline-actions" })
        {
            Assert.Matches(
                new Regex(
                    @"\." + Regex.Escape(block) + @"\s+\.btn\b[^{]*\{[^}]*min-height:\s*var\(--dt-touch-target\)",
                    RegexOptions.Singleline,
                    TimeSpan.FromSeconds(5)),
                query.Value);
        }
    }

    /// <summary>
    /// Both panels let a long unbroken value break rather than widen the dialog holding it.
    /// </summary>
    /// <remarks>
    /// `anywhere` rather than `break-word` in both places, and the difference is the whole point:
    /// only `anywhere` lets an intrinsic width shrink, which is what a flex item and a `1fr` grid
    /// track each resolve against. A name arrives from an account and nothing bounds one.
    /// </remarks>
    [Theory]
    [InlineData("DeliveryTimeline.razor.css", "dt-timeline-actor")]
    [InlineData("ProofView.razor.css", "dt-proof-facts dd")]
    public void A_value_with_nowhere_to_break_breaks_anyway(string file, string selector)
    {
        var css = WithoutComments(SharedMarkup.ReadComponent("Pages", file));

        Assert.Matches(
            new Regex(
                @"\." + Regex.Escape(selector).Replace(@"\ ", " ") + @"[^{]*\{[^}]*overflow-wrap:\s*anywhere",
                RegexOptions.Singleline,
                TimeSpan.FromSeconds(5)),
            css);
    }

    /// <summary>The one phone media query in a stylesheet, and what it contains.</summary>
    private static readonly Regex PhoneQuery = new(
        @"@media\s*\(max-width:\s*640\.98px\)\s*\{(?<body>.*)\}",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static string WithoutComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));
}
