using System.Text.RegularExpressions;
using DriveTrack.Application.Common;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Account;
using DriveTrack.Web.Components.Shared;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// Where a refusal appears, now that it has something specific to say.
/// <para>
/// <c>DtFailureBanner</c> alone answered "what was refused" and left "which box" to a label at the
/// top of a form half a screen tall. <c>DtFieldError</c> puts the sentence under the input that
/// produced it, and the banner keeps everything no input claimed — which is the half that has to be
/// asserted, because it is the half FR-83 is broken by. A screen that claimed a field it does not
/// render would make a refusal invisible, and nothing about the page would look wrong.
/// </para>
/// </summary>
public class FieldErrorTests
{
    /// <summary>
    /// A banner's claim list as a screen writes it: a literal, or <c>@Member</c> naming the
    /// screen's own constant or property. Both are resolved, so the two screens whose list is
    /// conditional are guarded like every other.
    /// </summary>
    private static readonly Regex InlineFieldsAttribute = new(
        @"InlineFields\s*=\s*""(?<value>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>A field-level control's own field, as a screen writes it out.</summary>
    private static readonly Regex FieldErrorField = new(
        @"<DtFieldError\s+Field\s*=\s*""(?<value>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>A whole <c>&lt;DtFailureBanner …&gt;</c> tag, however its attributes are spaced.</summary>
    private static readonly Regex BannerTag = new(
        @"<DtFailureBanner\b(?<attributes>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>A Razor comment, which is source rather than markup and must not satisfy a scan.</summary>
    private static readonly Regex RazorComment = new(
        @"@\*.*?\*@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline,
        TimeSpan.FromSeconds(5));

    /// <summary>A double-quoted string literal in C#.</summary>
    private static readonly Regex StringLiteral = new(
        @"""(?<value>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Fact]
    public async Task A_bounded_refusal_renders_under_the_input_it_is_about()
    {
        // The matrix's first row, against the box the spec's own example uses: an over-weight
        // parcel used to render the same sentence a missing one did. It now names the ceiling,
        // and it does so beneath the box rather than in a banner that does not say which box.
        var html = await ComponentRenderer.RenderAsync<DtFieldError>(
            new Dictionary<string, object?>
            {
                ["Field"] = "PackageWeightKg",
                ["Failures"] = (IReadOnlyList<ScreenFailure>)
                [
                    new ScreenFailure(
                        "PackageWeightKg",
                        nameof(ErrorCode.DELIVERY_PACKAGE_WEIGHT_TOO_LARGE)),
                ],
            });

        var text = SharedMarkup.TextOf(html);

        Assert.True(SharedMarkup.IsUkrainian(text), text);
        Assert.False(SharedMarkup.HasLatinWord(text), text);

        // The figure the validator holds, so the sentence is specific rather than merely different.
        Assert.Contains("9999999,999", text, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", html, StringComparison.Ordinal);

        // No label: the input's own <label> is directly above it, and repeating the name here is
        // the stutter the banner's Label() exists to avoid.
        Assert.DoesNotContain("<strong>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_control_shows_nothing_for_a_field_that_is_not_its_own()
    {
        // The claim the whole subtraction rests on. A control that rendered every failure would put
        // the weight's refusal under the note as well, and the banner's list would then be hiding
        // the sentences that were already duplicated.
        var html = await ComponentRenderer.RenderAsync<DtFieldError>(
            new Dictionary<string, object?>
            {
                ["Field"] = "DeliveryNotes",
                ["Failures"] = (IReadOnlyList<ScreenFailure>)
                [
                    new ScreenFailure("PackageWeightKg", nameof(ErrorCode.COMMON_FIELD_REQUIRED)),
                    new ScreenFailure(null, nameof(ErrorCode.COMMON_CONFLICT)),
                ],
            });

        Assert.Equal(string.Empty, SharedMarkup.TextOf(html));
    }

    [Fact]
    public async Task A_nested_failure_is_claimed_by_the_input_the_form_actually_has()
    {
        // `Pickup.Latitude` is what the validator wrote and `Pickup` is what the dialog renders.
        // FailureKeys collapses the path once, on the way in, so both the banner and the control
        // below the map key on the same word.
        var failures = FailureKeys.For(new ValidationException(
            ErrorCode.COMMON_VALIDATION_FAILED,
            "refused",
            [new FieldError("Pickup.Latitude", nameof(ErrorCode.COMMON_COORDINATE_OUT_OF_RANGE))]));

        var html = await ComponentRenderer.RenderAsync<DtFieldError>(
            new Dictionary<string, object?>
            {
                ["Field"] = "Pickup",
                ["Failures"] = failures,
            });

        var text = SharedMarkup.TextOf(html);

        Assert.True(SharedMarkup.IsUkrainian(text), text);
        Assert.False(SharedMarkup.HasLatinWord(text), text);
    }

    [Fact]
    public async Task Two_fields_failing_at_once_each_render_under_their_own_input()
    {
        // The matrix's third row. Rendered one control at a time, because that is how a form has
        // them: the pair is only two messages if each control keeps to its own field.
        IReadOnlyList<ScreenFailure> failures =
        [
            new ScreenFailure("Model", nameof(ErrorCode.COMMON_FIELD_REQUIRED)),
            new ScreenFailure("CapacityKg", nameof(ErrorCode.FLEET_CAPACITY_NOT_POSITIVE)),
        ];

        var model = await ComponentRenderer.RenderAsync<DtFieldError>(
            new Dictionary<string, object?> { ["Field"] = "Model", ["Failures"] = failures });

        var capacity = await ComponentRenderer.RenderAsync<DtFieldError>(
            new Dictionary<string, object?> { ["Field"] = "CapacityKg", ["Failures"] = failures });

        Assert.Equal(1, SharedMarkup.Occurrences(model, "role=\"alert\""));
        Assert.Equal(1, SharedMarkup.Occurrences(capacity, "role=\"alert\""));

        // Different sentences, which is the point of the two rules no longer sharing a code.
        Assert.NotEqual(SharedMarkup.TextOf(model), SharedMarkup.TextOf(capacity));
    }

    [Fact]
    public async Task A_failure_on_a_field_the_screen_does_not_render_still_reaches_the_banner()
    {
        // The matrix's fourth row, and FR-83 itself. The proof panel draws no coordinate box, so a
        // refusal naming CaptureLocation has no input to sit under — and the banner is what keeps
        // it from being nowhere at all.
        var html = await ComponentRenderer.RenderAsync<DtFailureBanner>(
            new Dictionary<string, object?>
            {
                ["InlineFields"] = "RecipientName,Assets",
                ["Failures"] = (IReadOnlyList<ScreenFailure>)
                [
                    new ScreenFailure("RecipientName", nameof(ErrorCode.COMMON_FIELD_REQUIRED)),
                    new ScreenFailure("CaptureLocation", nameof(ErrorCode.COMMON_FIELD_REQUIRED)),
                ],
            });

        Assert.Equal(1, SharedMarkup.Occurrences(html, "alert alert-danger"));

        var text = SharedMarkup.TextOf(html);

        // The unclaimed one, by its label, and not the claimed one.
        Assert.Contains("Місце вручення", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Хто отримав", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_that_names_no_field_is_never_claimed()
    {
        // The matrix's fifth row. A 403, a 404 and a 409 carry no field, so no list of field names
        // can subtract them however long it grows.
        var html = await ComponentRenderer.RenderAsync<DtFailureBanner>(
            new Dictionary<string, object?>
            {
                ["InlineFields"] = "Model,LicensePlate,CapacityKg,Mileage",
                ["Failures"] = (IReadOnlyList<ScreenFailure>)
                [
                    new ScreenFailure(null, nameof(ErrorCode.FLEET_VEHICLE_IN_USE)),
                ],
            });

        Assert.Equal(1, SharedMarkup.Occurrences(html, "alert alert-danger"));
        Assert.True(SharedMarkup.IsUkrainian(SharedMarkup.TextOf(html)));
    }

    [Fact]
    public async Task A_banner_given_no_list_still_shows_everything()
    {
        // The default, and the reason adding the parameter changed no screen that has not been
        // given per-field messages: an unset list claims nothing.
        var html = await ComponentRenderer.RenderAsync<DtFailureBanner>(
            new Dictionary<string, object?>
            {
                ["Failures"] = (IReadOnlyList<ScreenFailure>)
                [
                    new ScreenFailure("Model", nameof(ErrorCode.COMMON_FIELD_REQUIRED)),
                    new ScreenFailure("CapacityKg", nameof(ErrorCode.FLEET_CAPACITY_NOT_POSITIVE)),
                ],
            });

        Assert.Equal(2, SharedMarkup.Occurrences(html, "alert alert-danger"));
    }

    [Fact]
    public void Every_field_a_banner_claims_is_one_the_same_screen_renders_a_control_for()
    {
        // FR-83 as a standing guard rather than a reading of each screen. A banner subtracts what
        // it is told the screen shows, so the one way a refusal can go missing is a name in that
        // list with no DtFieldError under it — a rename, a deleted input, a copied attribute. None
        // of those looks wrong on the page, and no render test would reach them.
        //
        // Every branch of a computed list counts. Drivers and Profile are the two screens whose
        // boxes are conditional, which makes them the two most likely to claim something they are
        // not rendering - so an exemption here would leave the risk uncovered exactly where it is
        // highest.
        var offenders = new List<string>();

        foreach (var (path, source) in Screens())
        {
            var rendered = Rendered(source);

            offenders.AddRange(Claimed(source)
                .Where(field => !rendered.Contains(field))
                .Select(field => $"{Path.GetFileName(path)}: {field}"));
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_screen_that_renders_a_field_control_also_tells_its_banner_about_it()
    {
        // The other direction. A control added without the banner being told leaves the sentence on
        // the page twice — once under the box and once in the banner above it — which reads as a
        // defect rather than as emphasis.
        var offenders = new List<string>();

        foreach (var (path, source) in Screens())
        {
            var rendered = Rendered(source);

            if (rendered.Count == 0)
            {
                continue;
            }

            // The union across every branch: a control belonging to one branch is claimed by that
            // branch, which is all this direction asks. The forward test is what holds each branch
            // to rendering what it claims.
            var claimed = Claimed(source);

            offenders.AddRange(rendered
                .Where(field => !claimed.Contains(field))
                .Select(field => $"{Path.GetFileName(path)}: {field}"));
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_banner_on_a_screen_with_field_controls_is_given_the_list()
    {
        // A screen carries the banner more than once — at page level and again inside each modal,
        // because a dialog opened with showModal() leaves everything behind it inert. One of them
        // left without the list would show every claimed sentence a second time.
        // Matched on the parsed tag rather than on a literal substring: different spacing, a
        // reordered attribute or an occurrence inside a Razor comment would all make a substring
        // count agree while the page rendered a banner that claims nothing.
        var offenders = new List<string>();

        foreach (var (path, source) in Screens())
        {
            if (!FieldErrorField.IsMatch(source))
            {
                continue;
            }

            foreach (Match banner in BannerTag.Matches(source))
            {
                var attributes = banner.Groups["attributes"].Value;

                // The search boxes on the two delivery screens carry their own banner over their
                // own sink (DW-41). It holds one box's refusal and nothing the form's controls can
                // claim, so only a banner reading the screen's shared sink is required to subtract.
                if (!attributes.Contains("Failures=\"_failures\"", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!attributes.Contains("InlineFields", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(path)}: {banner.Value.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_field_control_addresses_its_field_by_name_and_never_by_a_lambda()
    {
        // The documented reason this product had no per-field messages: `For="@(() => ...)"` puts a
        // `=>` inside an attribute, which closes the tag as far as NFR-14's source scan is
        // concerned, and everything after it is then read as rendered English. The component exists
        // to avoid that construct, so the construct is asserted absent rather than remembered.
        var source = SharedMarkup.ReadShared("DtFieldError.razor");

        Assert.Contains("public string? Field", source, StringComparison.Ordinal);
        Assert.DoesNotContain("=>", GetMarkup(source), StringComparison.Ordinal);

        // And no screen reintroduces it through the framework's own component either.
        var offenders = Directory
            .GetFiles(SharedMarkup.ComponentsDirectory, "*.razor", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path)
                .Contains("<ValidationMessage", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>Every component, with its Razor comments stripped: comments are not markup.</summary>
    private static IEnumerable<(string Path, string Source)> Screens() =>
        Directory
            .GetFiles(SharedMarkup.ComponentsDirectory, "*.razor", SearchOption.AllDirectories)
            .Select(path => (path, RazorComment.Replace(File.ReadAllText(path), " ")));

    /// <summary>The fields a screen renders a <c>DtFieldError</c> for.</summary>
    private static HashSet<string> Rendered(string source) =>
        FieldErrorField
            .Matches(source)
            .Select(match => match.Groups["value"].Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every field the screen's banners claim, across every branch of a computed list.
    /// <para>
    /// A value of <c>@Member</c> is resolved to the declaration of that member in the same file and
    /// every string literal in it is taken as a branch. That is deliberately crude and exactly
    /// right for what is being guarded: the declaration is one statement holding one constant or
    /// one conditional, and taking every literal in it means no branch can be the one nobody looked
    /// at.
    /// </para>
    /// </summary>
    private static HashSet<string> Claimed(string source) =>
        InlineFieldsAttribute
            .Matches(source)
            .Select(match => match.Groups["value"].Value)
            .SelectMany(value => value.StartsWith('@') ? Branches(source, value[1..]) : [value])
            .SelectMany(FailureKeys.Names)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The string literals in a member's declaration - one per branch it can answer.</summary>
    private static string[] Branches(string source, string member)
    {
        var declaration = new Regex(
            @"\bstring\s+" + Regex.Escape(member) + @"\s*(?:=>|=)(?<body>[^;]*);",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5))
            .Match(source);

        Assert.True(declaration.Success, $"No declaration of {member} to resolve InlineFields from.");

        var branches = StringLiteral
            .Matches(declaration.Groups["body"].Value)
            .Select(literal => literal.Groups["value"].Value)
            .ToArray();

        Assert.NotEmpty(branches);

        return branches;
    }

    /// <summary>
    /// The part of a component that is actually rendered: everything before <c>@code</c>, with the
    /// Razor comments taken out. The comments have to go, because the one in <c>DtFieldError</c>
    /// quotes the construct this test forbids in order to explain why it is forbidden.
    /// </summary>
    private static string GetMarkup(string source)
    {
        var index = source.IndexOf("@code", StringComparison.Ordinal);
        var markup = index < 0 ? source : source[..index];

        return Regex.Replace(
            markup,
            @"@\*.*?\*@",
            " ",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));
    }
}
