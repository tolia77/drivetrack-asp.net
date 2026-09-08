using System.Text.RegularExpressions;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Components.Shared;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// The standing house rules for <c>Components/Shared/</c> that a render test does not reach.
/// <para>
/// Every one of these guards a failure that ships looking fine. A hard-coded colour in an icon
/// looks correct until the palette changes around it; a component that calls itself shared but
/// lives beside one screen is the drift AD-28 and this story exist to stop; a second
/// <c>ErrorBoundary</c> catches a failure before FR-13's single handler ever sees it.
/// </para>
/// </summary>
public class SharedComponentTests
{
    /// <summary>A paint attribute inside inline SVG markup.</summary>
    private static readonly Regex PaintAttribute = new(
        @"\b(?:fill|stroke|stop-color|flood-color|lighting-color)\s*=\s*""(?<value>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void There_are_shared_components_to_scan()
    {
        // Vacuity guard: the scan set is derived from directory layout, which a later story is
        // free to change, and an empty set makes every assertion below trivially true.
        Assert.NotEmpty(SharedMarkup.SharedComponents());
    }

    [Fact]
    public void An_icon_takes_its_colour_from_the_text_it_sits_beside()
    {
        // `currentColor` is the one colour keyword NFR-29's scanner allows, and it is not a
        // concession: it means a glyph follows whatever token coloured its surroundings, so the
        // same icon reads correctly on a card and on the navy navigation without a second rule.
        var offenders = new List<string>();

        foreach (var path in SharedMarkup.SharedComponents())
        {
            foreach (Match paint in PaintAttribute.Matches(File.ReadAllText(path)))
            {
                var value = paint.Groups["value"].Value.Trim();

                if (!string.Equals(value, "currentColor", StringComparison.Ordinal)
                    && !string.Equals(value, "none", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(path)}: {paint.Value}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_named_icon_is_actually_drawn()
    {
        // The defect this closes is the one the scaffold shipped: three `bi-*-nav-menu` spans with
        // no rule behind them, rendering an empty box on every screen while the build said nothing.
        // A name in the enum with no path in the markup is the same failure wearing a type.
        var markup = SharedMarkup.ReadShared("Icon.razor");

        var undrawn = Enum.GetValues<IconName>()
            .Where(name => !markup.Contains($"Name == IconName.{name}", StringComparison.Ordinal))
            .Select(name => name.ToString())
            .ToArray();

        Assert.Empty(undrawn);

        // And the other direction: a glyph drawn for a name the enum no longer has is dead markup.
        var drawn = Regex.Matches(
                markup,
                @"Name == IconName\.(?<name>[A-Za-z]+)",
                RegexOptions.None,
                TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Enum.GetValues<IconName>().Length, drawn.Length);
    }

    [Fact]
    public void The_icon_markup_is_inline_and_needs_no_second_stylesheet()
    {
        // NFR-24 without a webfont, a sprite or a third <link>: one <svg> the component writes
        // itself, which is also why it renders identically on first paint and offline.
        var markup = SharedMarkup.ReadShared("Icon.razor");

        Assert.Contains(@"viewBox=""0 0 16 16""", markup, StringComparison.Ordinal);
        Assert.Contains(@"aria-hidden=""true""", markup, StringComparison.Ordinal);
        Assert.Contains(@"focusable=""false""", markup, StringComparison.Ordinal);

        // Decorative by contract: an icon always travels with its text, so it must not be
        // announced twice.
        Assert.DoesNotContain("<img", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"bi", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shared_vocabulary_lives_in_one_folder()
    {
        // A "shared" component filed beside a single screen is how a vocabulary becomes a
        // convention nobody follows.
        var strays = Directory
            .GetFiles(SharedMarkup.ComponentsDirectory, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(path => Path.GetFileName(path).StartsWith("Dt", StringComparison.Ordinal))
            .Where(path => !path.StartsWith(SharedMarkup.SharedDirectory, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(strays);

        // And the ones this story added are all there.
        foreach (var expected in new[]
                 {
                     "Icon.razor", "DtDialog.razor", "DtConfirmDialog.razor",
                     "DtDataTable.razor", "DtMap.razor",
                 })
        {
            Assert.True(
                File.Exists(Path.Combine(SharedMarkup.SharedDirectory, expected)),
                $"Components/Shared/{expected} is missing.");
        }
    }

    [Fact]
    public void No_new_shared_component_declares_an_error_boundary_of_its_own()
    {
        // FR-13: SessionExpiryBoundary is the one error surface, and it is the only file here
        // allowed to name an ErrorBoundary. A second one catches a failure before the shared
        // handler sees it, and the screen shows a different notice than the product decided on.
        var offenders = SharedMarkup.SharedComponents()
            .Where(path => !Path.GetFileName(path)
                .Equals("SessionExpiryBoundary.razor", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("<ErrorBoundary", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_shared_component_reaches_for_a_second_dialog_implementation()
    {
        // The dialog's behaviour is the platform's plus one module of ours. Wiring Bootstrap's
        // modal beside it would mean a global script, framework data attributes, and two answers
        // to what "modal" means.
        var offenders = SharedMarkup.SharedComponents()
            .Where(path =>
            {
                var source = File.ReadAllText(path);

                return source.Contains("data-bs-", StringComparison.Ordinal)
                    || source.Contains("modal fade", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_shared_component_binds_to_a_DTO_rather_than_to_a_domain_entity()
    {
        // AD-1 and AD-17 at the component tier: the map takes MapLocation, the application layer's
        // shared coordinate pair, and not the domain's Location - which carries a cached address
        // and a resolution timestamp no map has any business knowing about.
        var map = SharedMarkup.ReadShared("DtMap.razor");

        Assert.Contains("MapLocation", map, StringComparison.Ordinal);

        var offenders = SharedMarkup.SharedComponents()
            .Where(path => File.ReadAllText(path)
                .Contains("DriveTrack.Domain", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }
}
