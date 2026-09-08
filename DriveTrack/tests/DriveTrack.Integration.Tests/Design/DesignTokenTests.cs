using System.Text.RegularExpressions;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Design;

/// <summary>
/// AD-28 and NFR-29 as a standing guard rather than a review convention.
/// <para>
/// The scaffold this story replaced held roughly seventy literal colours, sizes, radii and
/// shadows spread across four stylesheets, and every one of them arrived the same way: someone
/// needed a value, typed it, and moved on. Reviewing for that does not scale, so the rule is
/// asserted over the source. A declaration of a <em>visual</em> property - one that names a
/// palette value rather than a geometry - must resolve entirely to token references. Layout
/// figures (<c>width</c>, <c>height</c>, <c>z-index</c>, <c>flex</c>, breakpoints) are not
/// palette values and are deliberately out of scope.
/// </para>
/// <para>
/// <c>Styles/_tokens.scss</c> is the one exemption: it is where the literals live, and a rule
/// that forbade them everywhere would forbid the system from having any values at all.
/// <c>Styles/vendor/**</c> is unmodified third-party source and is not ours to retokenize.
/// </para>
/// <para>
/// The class also asserts the compiled theme, because a token layer that never reaches the
/// browser is a naming convention. Those assertions read
/// <c>wwwroot/css/app.css</c>, which AspNetCore.SassCompiler produces during the build of
/// DriveTrack.Web - a project this test assembly references, so it is always present.
/// </para>
/// </summary>
public class DesignTokenTests
{
    // -------------------------------------------------------------------------------------
    // What counts as a visual property
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Properties whose value is a palette value.
    /// <para>
    /// <c>background-image</c> is named individually rather than folded into a
    /// <c>background</c> family, because <c>background-repeat</c>, <c>-position</c> and
    /// <c>-size</c> are geometry and must stay unscanned. It has to be here all the same: a
    /// <c>linear-gradient()</c> is a palette value wearing an image's property name, and the
    /// scaffold's sidebar gradient - the story's headline offender - was written exactly that
    /// way. A <c>url()</c> is stripped before the check, so an inline SVG asset still passes.
    /// </para>
    /// <para>
    /// <c>outline</c> is here for the same reason: the scaffold's validation rules coloured
    /// through it, and an outline is as visible as a border.
    /// </para>
    /// </summary>
    private static readonly string[] VisualProperties =
    [
        "color",
        "background",
        "background-color",
        "background-image",
        "box-shadow",
        "text-shadow",
        "outline",
        "fill",
        "stroke",
        "text-decoration",
        "text-decoration-color",
        "text-emphasis-color",
        "outline-color",
        "column-rule-color",
        "scrollbar-color",
        "filter",
        "backdrop-filter",
        "mask-image",
        "accent-color",
        "caret-color",
        "font-family",
        "font-size",
        "font-weight",
        "line-height",
        "gap",
        "row-gap",
        "column-gap",
    ];

    /// <summary>
    /// Property families where every member is visual: <c>border</c>, <c>border-radius</c>,
    /// <c>border-bottom</c>, <c>padding-left</c>, <c>margin-inline</c> and the rest.
    /// </summary>
    private static readonly string[] VisualPropertyFamilies = ["border", "padding", "margin"];

    /// <summary>
    /// CSS-wide keywords that are not all-lowercase and would otherwise read as literals.
    /// Kept as a named exception so the "a keyword is lowercase" rule stays simple, and so a
    /// bare <c>Arial</c> in a font stack is still caught.
    /// </summary>
    private static readonly HashSet<string> AllowedKeywords =
        new(StringComparer.Ordinal) { "currentColor", "currentcolor" };

    /// <summary>
    /// The generic font families. A stack may name these and nothing else: every real family
    /// is a palette-tier decision that belongs in <c>_tokens.scss</c>. Without this the
    /// lowercase half of a stack - <c>arial, helvetica</c> - satisfied the keyword rule and
    /// only a capitalised <c>Arial</c> was ever caught.
    /// </summary>
    private static readonly HashSet<string> GenericFontFamilies = new(StringComparer.Ordinal)
    {
        "serif", "sans-serif", "monospace", "cursive", "fantasy", "system-ui",
        "ui-serif", "ui-sans-serif", "ui-monospace", "ui-rounded", "math", "emoji", "fangsong",
    };

    /// <summary>
    /// A vendor prefix. <c>-webkit-text-fill-color</c> is <c>text-fill-color</c> wearing a hat,
    /// and a rule that could be sidestepped by adding one would not be a rule.
    /// </summary>
    private static readonly Regex VendorPrefix = new(
        @"^-(?:webkit|moz|ms|o)-",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A proportion or an angle: <c>50%</c>, <c>100%</c>, <c>180deg</c>. Neither names a
    /// palette value - one is relative geometry and the other a direction - so both are out of
    /// scope in the same way <c>width</c> and <c>z-index</c> are. This is what lets a fully
    /// tokenized <c>linear-gradient(180deg, var(--a), var(--b))</c> through, and it is why the
    /// matrix lists <c>border-radius: 50%</c> as a non-offender. Absolute lengths stay caught.
    /// </summary>
    private static readonly Regex Proportion = new(
        @"^-?\d+(?:\.\d+)?(?:%|deg|grad|rad|turn)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The CSS named colours. <c>transparent</c> and <c>currentColor</c> are absent on purpose:
    /// they are keywords that name no palette value, so a component may say either.
    /// </summary>
    private static readonly HashSet<string> NamedColours = new(
        """
        aliceblue antiquewhite aqua aquamarine azure beige bisque black blanchedalmond blue
        blueviolet brown burlywood cadetblue chartreuse chocolate coral cornflowerblue cornsilk
        crimson cyan darkblue darkcyan darkgoldenrod darkgray darkgreen darkgrey darkkhaki
        darkmagenta darkolivegreen darkorange darkorchid darkred darksalmon darkseagreen
        darkslateblue darkslategray darkslategrey darkturquoise darkviolet deeppink deepskyblue
        dimgray dimgrey dodgerblue firebrick floralwhite forestgreen fuchsia gainsboro ghostwhite
        gold goldenrod gray green greenyellow grey honeydew hotpink indianred indigo ivory khaki
        lavender lavenderblush lawngreen lemonchiffon lightblue lightcoral lightcyan
        lightgoldenrodyellow lightgray lightgreen lightgrey lightpink lightsalmon lightseagreen
        lightskyblue lightslategray lightslategrey lightsteelblue lightyellow lime limegreen linen
        magenta maroon mediumaquamarine mediumblue mediumorchid mediumpurple mediumseagreen
        mediumslateblue mediumspringgreen mediumturquoise mediumvioletred midnightblue mintcream
        mistyrose moccasin navajowhite navy oldlace olive olivedrab orange orangered orchid
        palegoldenrod palegreen paleturquoise palevioletred papayawhip peachpuff peru pink plum
        powderblue purple rebeccapurple red rosybrown royalblue saddlebrown salmon sandybrown
        seagreen seashell sienna silver skyblue slateblue slategray slategrey snow springgreen
        steelblue tan teal thistle tomato turquoise violet wheat white whitesmoke yellow
        yellowgreen
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    // -------------------------------------------------------------------------------------
    // Reading declarations out of source
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// A CSS declaration. The value class excludes <c>{</c>, which is what stops a selector
    /// from matching: <c>.top-row ::deep a:hover {</c> offers a name and a colon, but the
    /// braces of its own block sit between it and any terminator, so it can never complete.
    /// </summary>
    private static readonly Regex DeclarationPattern = new(
        @"(?<name>[-$A-Za-z][-$A-Za-z0-9]*)\s*:\s*(?<value>(?:[^;{}]|#\{[^}]*\})*)[;}]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A <c>style</c> attribute in markup: the same rule reaches inline styles. Both quote
    /// forms, because Razor accepts either and a rule that only saw one would be a rule with a
    /// documented way around it.
    /// </summary>
    private static readonly Regex InlineStylePattern = new(
        @"style\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5));

    private static readonly Regex BlockComment = new(
        @"/\*.*?\*/",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A <c>//</c> comment, whole-line or trailing. Prose about a rule is not a breach of it,
    /// and the old whole-line-only anchor meant a trailing <c>// color: #fff</c> was scanned as
    /// though it were code. Safe to run unanchored because <see cref="Preprocess"/> removes
    /// <c>url()</c> first, so the <c>//</c> in a <c>http://</c> inside a data URI is long gone.
    /// </summary>
    private static readonly Regex LineComment = new(
        @"//[^\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A <c>url()</c> and everything in it. An inline SVG is an asset, and the semicolons and
    /// percent-encoded colours inside one would otherwise be read as declarations.
    /// </summary>
    private static readonly Regex UrlReference = new(
        @"url\((?:[^()]|\([^()]*\))*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex VarReference = new(
        @"var\(\s*--[^()]*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A reference to one of our own tokens. Only the <c>var()</c> form matches, so the
    /// <c>--dt-#{$name}</c> declarations in <c>_theme.scss</c> are not mistaken for uses.
    /// </summary>
    private static readonly Regex DesignTokenReference = new(
        @"var\(\s*--dt-(?<name>[a-z0-9-]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex SassVariable = new(
        @"\$[A-Za-z_][-A-Za-z0-9_]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex Interpolation = new(
        @"#\{[^}]*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex HexColour = new(
        @"#[0-9a-fA-F]{3,8}(?![0-9a-zA-Z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex ColourFunction = new(
        @"\b(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch|color|color-mix)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex Keyword = new(
        @"^[a-z][a-z-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>A paint attribute inside an inline SVG asset: <c>fill='white'</c>.</summary>
    private static readonly Regex SvgPaintAttribute = new(
        @"\b(?:fill|stroke|stop-color|flood-color|lighting-color)\s*=\s*['""](?<value>[^'""]*)['""]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The <c>url()</c> assets allowed to carry a literal colour, identified by a fragment
    /// unique to each. There is exactly one, and
    /// <c>Only_the_recorded_url_asset_carries_a_literal_colour</c> holds it to that: the
    /// navigation toggler, whose reasoning is written out at the rule in
    /// <c>NavMenu.razor.css</c>. Every other icon is masked and takes its colour from a token.
    /// <para>
    /// An exemption list is a hole in a rule, so it is spelled as a path fragment rather than a
    /// selector or a file: an asset that moves keeps its exemption, and an asset that changes
    /// loses it and has to be argued for again.
    /// </para>
    /// </summary>
    private static readonly string[] RecordedUrlColourExemptions =
        ["M4 7h22M4 15h22M4 23h22"];

    /// <summary>
    /// A Bootstrap <c>-info</c> theme variant. The families are named rather than matched by a
    /// bare <c>-info</c> suffix, so an ordinary word ending in "info" is not a false positive.
    /// </summary>
    private static readonly Regex InfoVariant = new(
        @"\b(?:btn|btn-outline|alert|alert-link|text-bg|bg|border|text|link|list-group-item|table)-info\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>A <c>$dt-…:</c> variable declaration at the start of a line.</summary>
    private static readonly Regex SemanticVariable = new(
        @"^\s*\$dt-(?<name>[a-z0-9-]+)\s*:",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>An entry key in the <c>$dt-tokens</c> map in <c>_tokens.scss</c>.</summary>
    private static readonly Regex TokenMapEntry = new(
        @"^\s*""(?<name>[a-z0-9-]+)""\s*:",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    // =====================================================================================
    // The detector, asserted against the story's I/O matrix
    // =====================================================================================

    [Theory]
    // Accepted: the declaration resolves entirely to token references.
    [InlineData("color", "var(--dt-text-primary)", false)]
    [InlineData("background-color", "var(--bs-body-bg)", false)]
    [InlineData("padding", "$dt-space-3", false)]
    [InlineData("border-bottom", "var(--dt-border-width) solid var(--dt-border-subtle)", false)]
    // Accepted: a keyword and a unitless zero name no palette value.
    [InlineData("border", "none", false)]
    [InlineData("margin", "0", false)]
    [InlineData("color", "inherit", false)]
    [InlineData("background", "transparent", false)]
    // Accepted: layout is not a palette value, so these properties are not scanned at all -
    // including the geometry half of the background shorthand family.
    [InlineData("width", "100%", false)]
    [InlineData("height", "100vh", false)]
    [InlineData("z-index", "1000", false)]
    [InlineData("flex", "1", false)]
    [InlineData("background-size", "1.75rem", false)]
    [InlineData("background-position", "center", false)]
    // Accepted: an inline SVG asset in background-image, which is scanned but url-stripped.
    [InlineData("background-image", "url(\"data:image/svg+xml,%3csvg fill='white'%3e%3c/svg%3e\")", false)]
    [InlineData("outline", "none", false)]
    // Rejected: colours by hex, by function and by name.
    [InlineData("background-color", "#f7f7f7", true)]
    [InlineData("background", "rgba(255, 255, 255, 0.1)", true)]
    [InlineData("color", "white", true)]
    [InlineData("background", "lightyellow", true)]
    // Rejected: literal lengths.
    [InlineData("padding", "1.1rem", true)]
    [InlineData("border-radius", "4px", true)]
    [InlineData("margin-left", "1.5rem", true)]
    // Rejected: literal type.
    [InlineData("font-family", "'Helvetica Neue', Arial", true)]
    [InlineData("font-size", "0.9rem", true)]
    [InlineData("font-weight", "600", true)]
    [InlineData("line-height", "1.5", true)]
    // Rejected: a shadow is a colour and three lengths.
    [InlineData("box-shadow", "0 3px 6px rgba(0, 0, 0, .3)", true)]
    // Rejected: the scaffold's sidebar gradient, and the outline it validated through.
    [InlineData("background-image", "linear-gradient(180deg, rgb(5, 39, 103) 0%, #3a0647 70%)", true)]
    [InlineData("outline", "1px solid #26b050", true)]
    public void A_declaration_is_classified_as_the_matrix_specifies(
        string property,
        string value,
        bool isOffender)
    {
        var reason = Offence(property, value);

        Assert.Equal(isOffender, reason is not null);
    }

    // =====================================================================================
    // The scan
    // =====================================================================================

    [Fact]
    public void There_is_something_to_scan()
    {
        // Guards every assertion below: an empty file set makes them vacuously true, and the
        // scan set is derived from directory layout, which a later story is free to change.
        Assert.NotEmpty(MarkupFiles());
        Assert.NotEmpty(ScopedStylesheets());
        Assert.NotEmpty(ProjectStylesheets());
    }

    [Fact]
    public void No_component_or_project_stylesheet_declares_a_literal_visual_value()
    {
        var offenders = new List<string>();

        foreach (var path in ScopedStylesheets().Concat(ProjectStylesheets()))
        {
            var source = File.ReadAllText(path);

            // The bridge is the seam between the palette and Bootstrap, and it works entirely
            // in Sass variable names rather than CSS property names, so nothing in it would be
            // recognised as visual by name alone.
            var isBridge = string.Equals(
                Path.GetFileName(path), "_bootstrap-bridge.scss", StringComparison.Ordinal);

            foreach (var offence in UrlAssetOffences(source))
            {
                offenders.Add($"{Path.GetFileName(path)}: {offence}");
            }

            foreach (Match declaration in DeclarationPattern.Matches(Preprocess(source)))
            {
                Record(
                    offenders,
                    path,
                    declaration.Groups["name"].Value,
                    declaration.Groups["value"].Value,
                    isBridge);
            }
        }

        foreach (var path in MarkupFiles())
        {
            var source = File.ReadAllText(path);

            foreach (var offence in UrlAssetOffences(source).Concat(MarkupOffences(source)))
            {
                offenders.Add($"{Path.GetFileName(path)}: {offence}");
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The exemption list is a hole in NFR-29, so its size is itself asserted: one entry, and
    /// that entry has to still be reachable in the component tree. An exemption for an asset
    /// that has since been deleted or masked would otherwise sit there indefinitely, widening
    /// the rule for nothing.
    /// </summary>
    [Fact]
    public void Only_the_recorded_url_asset_carries_a_literal_colour()
    {
        var exemption = Assert.Single(RecordedUrlColourExemptions);

        var live = ScopedStylesheets()
            .Concat(ProjectStylesheets())
            .Concat(MarkupFiles())
            .Any(path => File.ReadAllText(path).Contains(exemption, StringComparison.Ordinal));

        Assert.True(
            live,
            $"The recorded url() colour exemption '{exemption}' matches no asset in the "
                + "component tree. Delete it rather than leaving the rule widened.");
    }

    /// <summary>
    /// The other direction of the map contract. <c>_tokens.scss</c> says it itself: "a semantic
    /// token that is not in the map does not exist at runtime", which makes a semantic variable
    /// declared and then forgotten in the map the failure worth catching - it ships as an
    /// unstyled element with a green suite, and the dangling-token check only notices once
    /// somebody writes a <c>var()</c> for it. The publication test walks map to CSS; this walks
    /// declaration to map, and between them the two tiers cannot drift apart.
    /// </summary>
    /// <summary>
    /// NFR-23's action vocabulary has no <c>info</c>, and the palette has no hue for one, so
    /// <c>$info</c> is aliased to the link colour in the bridge - which makes every
    /// <c>-info</c> variant render identically to its <c>-primary</c> counterpart. A component
    /// that reaches for one expecting a distinction gets none, and the screen looks correct
    /// until the two appear side by side. Asserted rather than commented, because the comment
    /// lives in the bridge and the component author is not reading the bridge.
    /// <para>
    /// Scoped to components: the bridge itself names these classes while explaining the rule.
    /// </para>
    /// </summary>
    [Fact]
    public void No_component_uses_the_info_variant()
    {
        var offenders = new List<string>();

        foreach (var path in MarkupFiles().Concat(ScopedStylesheets()))
        {
            foreach (Match use in InfoVariant.Matches(Preprocess(File.ReadAllText(path))))
            {
                offenders.Add($"{Path.GetFileName(path)}: {use.Value}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_semantic_token_variable_is_in_the_map()
    {
        var published = SemanticTokenNames().ToHashSet(StringComparer.Ordinal);

        var missing = SemanticTokenVariables()
            .Where(name => !published.Contains(name))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_token_a_stylesheet_references_exists()
    {
        // The scan above strips `var()` unconditionally, which is right - it cannot know what a
        // Bootstrap or framework custom property resolves to. The cost is that a misspelled
        // `--dt-` name reads as perfectly clean and ships as an unstyled element with a green
        // suite. `--dt-` is ours, so a reference to one that is not in the map is a typo, and
        // this is the only place that can say so.
        var published = SemanticTokenNames().ToHashSet(StringComparer.Ordinal);
        var dangling = new List<string>();

        // Markup is scanned alongside the stylesheets: the literal scan already reaches inline
        // `style` attributes, so a `--dt-` typo inside one was invisible to both halves of the
        // rule - stripped as a `var()` by the literal scan, and never opened by this one.
        //
        // Read through Preprocess rather than raw, so a token named in prose inside a comment
        // is not counted as a reference and reported as dangling.
        foreach (var path in ScopedStylesheets().Concat(ProjectStylesheets()).Concat(MarkupFiles()))
        {
            var source = Preprocess(File.ReadAllText(path));

            foreach (Match reference in DesignTokenReference.Matches(source))
            {
                var name = reference.Groups["name"].Value;

                if (!published.Contains(name))
                {
                    dangling.Add($"{Path.GetFileName(path)}: --dt-{name}");
                }
            }
        }

        Assert.Empty(dangling);
    }

    [Fact]
    public void An_inline_style_in_markup_is_scanned_like_a_stylesheet()
    {
        // The markup path is a separate reader from the stylesheet one, so the matrix above
        // does not cover it. Without this, `<div style="color:#333">` would be the one place a
        // literal could still be written.
        var offences = MarkupOffences("""<div class="page" style="color:#333">text</div>""");

        Assert.Contains(offences, offence => offence.Contains("color", StringComparison.Ordinal));
    }

    // =====================================================================================
    // The compiled theme
    // =====================================================================================

    [Fact]
    public void The_theme_is_compiled_into_the_web_root()
    {
        var theme = new FileInfo(CompiledThemePath);

        Assert.True(theme.Exists, $"Expected the Sass build to produce '{CompiledThemePath}'.");
        Assert.True(theme.Length > 0, "The compiled theme is empty.");
    }

    [Theory]
    // Bootstrap's own defaults sit in the third column. Each pair proves the framework was
    // *themed* - its Sass variables assigned before its import - rather than overridden after
    // the fact, which would leave the default in the custom property and a duplicate below it.
    [InlineData("--bs-primary", "#2563eb", "#0d6efd")]
    [InlineData("--bs-body-bg", "#f4f6fa", "#fff")]
    [InlineData("--bs-border-radius", "0.5rem", "0.375rem")]
    [InlineData("--bs-body-font-family", "system-ui, -apple-system, \"Segoe UI\", Roboto, \"Helvetica Neue\", Arial, sans-serif", "\"Noto Sans\"")]
    public void A_bootstrap_custom_property_carries_the_token_value(
        string property,
        string tokenValue,
        string bootstrapDefault)
    {
        // The third column has to differ from the second, or the case proves nothing: a pair
        // where the token value happens to equal Bootstrap's own default would pass whether the
        // framework was themed or left stock. Asserted on the DATA, because the old
        // `DoesNotContain` on the declaration could never fail once `Equal` above had pinned it
        // - it read as a second check and was one branch of the first.
        Assert.NotEqual(bootstrapDefault, tokenValue, StringComparer.Ordinal);

        Assert.Equal(tokenValue, Declaration(CompiledTheme.Value, property));
    }

    [Theory]
    // The tokens the story names explicitly: the three AD-28 action colours and the three
    // FR-98 rating colours. The map-driven assertion below covers the rest.
    [InlineData("--dt-action-create")]
    [InlineData("--dt-action-edit")]
    [InlineData("--dt-action-destructive")]
    [InlineData("--dt-rating-favourable")]
    [InlineData("--dt-rating-neutral")]
    [InlineData("--dt-rating-unfavourable")]
    public void A_named_semantic_token_reaches_the_browser(string property)
    {
        Assert.False(
            string.IsNullOrWhiteSpace(Declaration(CompiledTheme.Value, property)),
            $"The compiled theme declares no '{property}'.");
    }

    [Fact]
    public void The_token_map_has_entries()
    {
        // Vacuity guard for the assertion below: `$dt-tokens` is read by regex, so a change to
        // the map's formatting could silently reduce it to nothing.
        Assert.NotEmpty(SemanticTokenNames());
    }

    [Fact]
    public void Every_semantic_token_is_published_as_a_custom_property()
    {
        // Scoped component CSS is compiled separately from the theme, so a `.razor.css` can
        // only reach the token layer through a custom property. A semantic token that exists
        // in Sass and not at runtime is a token no component can use.
        var missing = SemanticTokenNames()
            .Where(name => !CompiledTheme.Value.Contains($"--dt-{name}:", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void The_theme_ships_no_dark_colour_mode()
    {
        // NFR-21: one light theme, no switch. `$enable-dark-mode: false` in the bridge drops
        // Bootstrap's dark custom-property block and every component's dark variant.
        var css = CompiledTheme.Value;

        Assert.DoesNotContain("prefers-color-scheme", css, StringComparison.Ordinal);

        // Bootstrap emits `.navbar-dark, .navbar[data-bs-theme=dark]` unconditionally - it is
        // the deprecated alias for the `.navbar-dark` class the sidebar already uses, not a
        // colour-mode block. Every other occurrence would be one, so the counts must agree.
        var navbarAlias = Occurrences(css, ".navbar[data-bs-theme=dark]");

        // Asserted first, because the comparison below is satisfied by two zeros. If Bootstrap
        // ever stops emitting the alias - or emits it in another form - the counts would agree
        // while a real dark block sat beside them unnoticed, and this test would be checking
        // nothing at all.
        Assert.True(
            navbarAlias > 0,
            "The `.navbar[data-bs-theme=dark]` alias is gone, so the comparison below no "
                + "longer distinguishes it from a real dark colour-mode block.");

        Assert.Equal(Occurrences(css, "[data-bs-theme=dark]"), navbarAlias);
    }

    // =====================================================================================
    // The scaffold is gone
    // =====================================================================================

    [Fact]
    public void The_shell_links_one_stylesheet_bundle_and_the_scoped_bundle()
    {
        var shell = File.ReadAllText(Path.Combine(WebProject, "Components", "App.razor"));
        // Both quote forms and either attribute order: the count is the whole point of this
        // test, so a third stylesheet written `rel='stylesheet'` - or with `href` first - must
        // not be the one that slips past it. Razor accepts every one of those spellings.
        var links = Regex.Matches(
            shell,
            @"<link\b[^>]*\brel\s*=\s*[""']?stylesheet\b[^>]*>",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(5));

        Assert.Equal(2, links.Count);
        Assert.Contains(links, link => link.Value.Contains("css/app.css", StringComparison.Ordinal));
        Assert.Contains(links, link => link.Value.Contains("DriveTrack.Web.styles.css", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_references_the_removed_scaffold_stylesheets()
    {
        var offenders = ProjectFiles()
            .Where(path =>
            {
                var text = File.ReadAllText(path);

                return text.Contains("lib/bootstrap/css", StringComparison.Ordinal)
                    || text.Contains(@"Assets[""app.css""]", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_scaffold_stylesheets_are_deleted_and_the_bootstrap_script_is_kept()
    {
        Assert.False(File.Exists(Path.Combine(WebProject, "wwwroot", "app.css")));
        Assert.False(Directory.Exists(Path.Combine(WebProject, "wwwroot", "lib", "bootstrap", "css")));

        // Same 5.3.3 as the vendored Sass, and Story 3.2's substrate for dialogs and the nav.
        Assert.True(File.Exists(BootstrapScriptPath));
    }

    [Fact]
    public void The_vendored_sass_and_the_shipped_script_are_the_same_bootstrap()
    {
        // The two halves of Bootstrap now arrive by different routes - the Sass was vendored by
        // hand into Styles/vendor, the script has been in wwwroot since the scaffold - so
        // nothing but this stops one being upgraded without the other. A page whose CSS and JS
        // disagree about a component's class names fails in ways that look like application
        // bugs, so the version is pinned rather than merely documented.
        var sassBanner = File.ReadAllText(Path.Combine(
            StylesDirectory, "vendor", "bootstrap", "mixins", "_banner.scss"));

        // Only the banner is read from the script: it is a minified bundle, and its first line
        // is the one part of it that is meant to be human-readable.
        // ReadBlock, not Read: a single Read is allowed to return fewer characters than asked
        // for, which would truncate the banner mid-version and fail BootstrapVersion on a
        // perfectly correct tree.
        using var reader = new StreamReader(BootstrapScriptPath);
        var scriptBanner = new char[512];
        var read = reader.ReadBlock(scriptBanner, 0, scriptBanner.Length);

        Assert.Equal("5.3.3", BootstrapVersion(sassBanner));
        Assert.Equal("5.3.3", BootstrapVersion(new string(scriptBanner, 0, read)));
    }

    /// <summary>The version out of a Bootstrap <c>/*! Bootstrap … v5.3.3 … */</c> banner.</summary>
    private static string BootstrapVersion(string banner)
    {
        var match = Regex.Match(
            banner,
            @"Bootstrap[^\r\n]*?\bv(?<version>\d+\.\d+\.\d+)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"No Bootstrap version banner found in: {banner.Trim()}");

        return match.Groups["version"].Value;
    }

    // =====================================================================================
    // Detection
    // =====================================================================================

    /// <summary>
    /// Why <paramref name="value"/> is a literal, or <c>null</c> if the declaration is clean or
    /// names a property that is not visual.
    /// </summary>
    private static string? Offence(string property, string value, bool everyDeclarationIsVisual = false)
    {
        var declared = property.Trim();
        var isCustomProperty = declared.StartsWith("--", StringComparison.Ordinal);
        var name = declared.TrimStart('$');

        // Three ways in, because the old single gate - "is this the name of a visual CSS
        // property?" - let two whole categories past:
        //
        //   everyDeclarationIsVisual  the Bootstrap bridge assigns Sass VARIABLES, and
        //                            `$card-bg`, `$primary`, `$link-color`, `$font-size-base`
        //                            are not CSS property names. The file whose entire job is
        //                            to be the seam between the palette and Bootstrap was
        //                            therefore unscanned; a raw hex there reached the browser
        //                            with a green suite.
        //   isCustomProperty         `--mine: #f00` paired with `color: var(--mine)` laundered
        //                            any literal past both halves of the rule: the consuming
        //                            side strips `var()`, and the declaring side used to
        //                            return early. `_tokens.scss` is excluded from the scan
        //                            set outright, so the tier allowed raw values is unharmed.
        if (!everyDeclarationIsVisual && !isCustomProperty && !IsVisual(name))
        {
            return null;
        }

        var remainder = StripReferences(value);

        if (ColourFunction.IsMatch(remainder))
        {
            return "colour function";
        }

        if (HexColour.IsMatch(remainder))
        {
            return "hex colour";
        }

        var isFontStack = name.EndsWith("font-family", StringComparison.Ordinal);

        foreach (var piece in remainder.Split(
                     [' ', '\t', '\r', '\n', ',', '/', '(', ')'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (piece is "0" or "-0" || AllowedKeywords.Contains(piece))
            {
                continue;
            }

            if (NamedColours.Contains(piece))
            {
                return "named colour";
            }

            // A proportion or an angle is geometry, not palette, so it is allowed even inside a
            // scanned property. Absolute lengths are not: a radius or a shadow figure written
            // as `7px` is exactly what NFR-29 exists to catch.
            if (Proportion.IsMatch(piece))
            {
                continue;
            }

            if (isFontStack && !GenericFontFamilies.Contains(piece))
            {
                return "font family";
            }

            if (!Keyword.IsMatch(piece))
            {
                return "literal value";
            }
        }

        return null;
    }

    private static bool IsVisual(string name)
    {
        name = VendorPrefix.Replace(name, string.Empty);

        return VisualProperties.Contains(name, StringComparer.Ordinal)
            || Array.Exists(
                VisualPropertyFamilies,
                family => string.Equals(name, family, StringComparison.Ordinal)
                    || name.StartsWith(family + "-", StringComparison.Ordinal));
    }

    /// <summary>
    /// The <c>url()</c> assets in <paramref name="source"/> that carry a literal colour.
    /// <para>
    /// <see cref="Preprocess"/> strips <c>url()</c> before declarations are read, because the
    /// semicolons and percent-encoded parentheses inside a data URI would otherwise be parsed
    /// as declarations. That strip also carried every colour inside the asset out of the scan,
    /// which is how an inline SVG kept <c>fill='white'</c> and a percent-encoded
    /// <c>rgba(...)</c> stroke - the matrix's own worked example of an offender - while the
    /// suite stayed green. So the assets are read here, from the raw source, before any of it
    /// is stripped.
    /// </para>
    /// </summary>
    private static List<string> UrlAssetOffences(string source)
    {
        var offences = new List<string>();

        foreach (Match asset in UrlReference.Matches(source))
        {
            var text = Uri.UnescapeDataString(asset.Value);

            if (RecordedUrlColourExemptions.Any(
                    exempt => asset.Value.Contains(exempt, StringComparison.Ordinal)))
            {
                continue;
            }

            var reason = ColourFunction.IsMatch(text) ? "colour function"
                : HexColour.IsMatch(text) ? "hex colour"
                : SvgPaintAttribute.Matches(text)
                    .Select(paint => paint.Groups["value"].Value)
                    .FirstOrDefault(paint => NamedColours.Contains(paint)) is not null
                    ? "named colour"
                    : null;

            if (reason is not null)
            {
                offences.Add($"url() asset - {reason}");
            }
        }

        return offences;
    }

    /// <summary>Removes everything that is a reference rather than a value.</summary>
    private static string StripReferences(string value)
    {
        value = value.Replace("!important", " ", StringComparison.OrdinalIgnoreCase);
        value = UrlReference.Replace(value, " ");
        value = Interpolation.Replace(value, " ");
        value = SassVariable.Replace(value, " ");

        // A `var()` may carry a `var()` fallback, so the innermost is removed first and the
        // replacement is repeated until it stops changing anything.
        string previous;

        do
        {
            previous = value;
            value = VarReference.Replace(value, " ");
        }
        while (!string.Equals(previous, value, StringComparison.Ordinal));

        return value;
    }

    /// <summary>
    /// Strips everything that is not a declaration. Order matters: <c>url()</c> goes before
    /// <c>//</c> comments, so the <c>http://</c> inside a data URI cannot be read as the start
    /// of one. The <c>url()</c> assets themselves are checked separately, by
    /// <see cref="UrlAssetOffences"/>, which runs on the raw source before this.
    /// </summary>
    private static string Preprocess(string source) =>
        LineComment.Replace(UrlReference.Replace(BlockComment.Replace(source, " "), " "), " ");

    private static void Record(
        List<string> offenders,
        string path,
        string property,
        string value,
        bool everyDeclarationIsVisual = false)
    {
        var reason = Offence(property, value, everyDeclarationIsVisual);

        if (reason is not null)
        {
            offenders.Add(
                $"{Path.GetFileName(path)}: {property.Trim()}: {Collapse(value)} - {reason}");
        }
    }

    private static List<string> MarkupOffences(string markup)
    {
        var offences = new List<string>();

        foreach (Match style in InlineStylePattern.Matches(Preprocess(markup)))
        {
            foreach (var part in style.Groups["value"].Value.Split(';'))
            {
                var separator = part.IndexOf(':', StringComparison.Ordinal);

                if (separator <= 0)
                {
                    continue;
                }

                var property = part[..separator];
                var value = part[(separator + 1)..];
                var reason = Offence(property, value);

                if (reason is not null)
                {
                    offences.Add($"{property.Trim()}: {Collapse(value)} - {reason}");
                }
            }
        }

        return offences;
    }

    private static string Collapse(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(5));

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;

        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The value of a custom property declared in the compiled theme.</summary>
    private static string Declaration(string css, string property)
    {
        var match = Regex.Match(
            css,
            Regex.Escape(property) + @"\s*:\s*(?<value>[^;}]*)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    // =====================================================================================
    // The scan set
    // =====================================================================================

    private static string WebProject => RepositoryLayout.ProjectDirectory("DriveTrack.Web");

    private static string StylesDirectory => Path.Combine(WebProject, "Styles");

    private static string CompiledThemePath =>
        Path.Combine(WebProject, "wwwroot", "css", "app.css");

    private static string BootstrapScriptPath =>
        Path.Combine(WebProject, "wwwroot", "lib", "bootstrap", "js", "bootstrap.bundle.min.js");

    /// <summary>
    /// The compiled theme. The existence check is here rather than only in the test that
    /// asserts it, because a `Lazy` caches the exception it threw: without this, a run against
    /// an unbuilt tree fails five tests with a bare FileNotFoundException and says nothing
    /// about what to do.
    /// </summary>
    private static readonly Lazy<string> CompiledTheme = new(() =>
    {
        Assert.True(
            File.Exists(CompiledThemePath),
            $"The compiled theme is missing from '{CompiledThemePath}'. It is generated by the "
                + "Sass step of the DriveTrack.Web build; run `dotnet build DriveTrack/DriveTrack.sln`.");

        return File.ReadAllText(CompiledThemePath);
    });

    /// <summary>
    /// Every markup file in the project, wherever it lives. Filtered by suffix rather than by
    /// search pattern: on Windows a <c>*.razor</c> pattern also matches <c>*.razor.css</c>,
    /// which would put scoped stylesheets through the markup reader.
    /// </summary>
    private static string[] MarkupFiles() =>
        [.. ProjectFiles().Where(path => path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Every scoped stylesheet in the project. Deliberately not rooted at <c>Components/</c>:
    /// a scoped stylesheet a later story puts anywhere else would otherwise be unscanned while
    /// the vacuity guard kept passing.
    /// </summary>
    private static string[] ScopedStylesheets() =>
        [.. ProjectFiles().Where(path => path.EndsWith(".razor.css", StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// The project's own Sass, at any depth. <c>_tokens.scss</c> is where the literals are
    /// supposed to live and <c>Styles/vendor/</c> is Bootstrap's own source; everything else
    /// under <c>Styles/</c> is ours to keep tokenized, including whatever subdirectory a later
    /// story introduces.
    /// </summary>
    private static string[] ProjectStylesheets() =>
    [
        .. Directory.GetFiles(StylesDirectory, "*.scss", SearchOption.AllDirectories)
            .Where(path => !IsVendored(path))
            .Where(path => !string.Equals(
                Path.GetFileName(path), "_tokens.scss", StringComparison.Ordinal)),
    ];

    private static bool IsVendored(string path) =>
        path.Contains(
            $"{Path.DirectorySeparatorChar}vendor{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal);

    /// <summary>
    /// Every hand-written file in the web project: build output, the compiled web root and
    /// vendored third-party source are all excluded, so what remains is what this story is
    /// answerable for.
    /// </summary>
    private static IEnumerable<string> ProjectFiles() =>
        Directory.EnumerateFiles(WebProject, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !IsVendored(path))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}wwwroot{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// The semantic <c>$dt-</c> variables: everything declared after the "Semantics" divider and
    /// before the map. Bounded that way rather than by name, because the two tiers are told
    /// apart by which section they sit in - <c>$dt-navy-900</c> and <c>$dt-surface-nav</c> are
    /// the same shape.
    /// </summary>
    private static string[] SemanticTokenVariables()
    {
        var tokens = File.ReadAllText(Path.Combine(StylesDirectory, "_tokens.scss"));
        var semantics = tokens.IndexOf("// Semantics", StringComparison.Ordinal);
        var map = tokens.IndexOf("$dt-tokens:", StringComparison.Ordinal);

        Assert.True(
            semantics >= 0,
            "_tokens.scss no longer has a '// Semantics' divider, which is what tells the "
                + "reference tier from the semantic one.");
        Assert.True(map > semantics, "_tokens.scss no longer declares $dt-tokens after the semantics.");

        return
        [
            .. SemanticVariable.Matches(tokens[semantics..map])
                .Select(declaration => declaration.Groups["name"].Value),
        ];
    }

    /// <summary>The keys of the <c>$dt-tokens</c> map: the published semantic surface.</summary>
    private static string[] SemanticTokenNames()
    {
        var tokens = File.ReadAllText(Path.Combine(StylesDirectory, "_tokens.scss"));
        var map = tokens.IndexOf("$dt-tokens:", StringComparison.Ordinal);

        Assert.True(map >= 0, "_tokens.scss no longer declares a $dt-tokens map.");

        // Bounded at the map's own `);` rather than run to end of file: any later Sass map in
        // this file would otherwise contribute its keys to the published token surface, and a
        // token that does not exist would read as published.
        var close = tokens.IndexOf(");", map, StringComparison.Ordinal);

        Assert.True(close > map, "The $dt-tokens map in _tokens.scss is not closed.");

        return
        [
            .. TokenMapEntry.Matches(tokens[map..close]).Select(entry => entry.Groups["name"].Value),
        ];
    }
}
