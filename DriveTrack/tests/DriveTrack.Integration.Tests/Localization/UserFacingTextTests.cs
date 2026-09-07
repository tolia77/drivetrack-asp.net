using System.Text.RegularExpressions;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Localization;

/// <summary>
/// NFR-14 as a standing guard rather than a one-off clean-up.
/// <para>
/// The scaffolded English this story removed by hand would reappear the first time a later story
/// pastes in a generated component - which is how it arrived in the first place. So the rule is
/// asserted over the source: no rendered text node in any <c>.razor</c> file may contain a run of
/// Latin letters, because a literal that survives there is by definition one that never became a
/// resource key.
/// </para>
/// </summary>
public class UserFacingTextTests
{
    /// <summary>
    /// The one allowed Latin run: the product name, which is a proper noun and is not translated.
    /// Kept as an allowlist of exactly one so adding a second requires a deliberate decision.
    /// </summary>
    private static readonly string[] AllowedWords = ["DriveTrack"];

    /// <summary>
    /// A Razor expression: <c>@Token</c> plus an optional balanced index or call. The whitespace
    /// before the bracket matters: <c>@if (ShowRequestId)</c> is written with a space, and without
    /// it only <c>@if</c> would be removed, leaving the identifier behind as a false offender.
    /// </summary>
    private static readonly Regex RazorExpression = new(
        @"@[A-Za-z_][A-Za-z0-9_.]*(\s*\[[^\]]*\]|\s*\([^)]*\))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>Text between the end of one tag and the start of the next.</summary>
    private static readonly Regex TextNode = new(
        @">([^<]*)<",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>Three or more consecutive Latin letters: a word, not an artefact.</summary>
    private static readonly Regex LatinWord = new(
        @"[A-Za-z]{3,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The attributes a user actually reads. Scanning every attribute would drown in <c>class</c>,
    /// <c>href</c> and <c>id</c> values, which are markup rather than prose - so the set is named,
    /// and a new user-facing attribute joins it deliberately.
    /// </summary>
    private static readonly string[] UserFacingAttributes =
    [
        "title",
        "alt",
        "placeholder",
        "label",
        "aria-label",
        "aria-description",
        "aria-roledescription",
        "aria-valuetext",
        "aria-placeholder",
    ];

    /// <summary>
    /// The Razor directives that are compiler instructions rather than rendered markup. Only these
    /// are dropped: a line beginning with any other <c>@</c> expression - <c>@Body</c>,
    /// <c>@if (...)</c>, <c>@foreach (...)</c> - is markup, and dropping the whole line would take
    /// any English sitting after the expression with it.
    /// </summary>
    private static readonly string[] Directives =
    [
        "@page",
        "@using",
        "@inject",
        "@inherits",
        "@implements",
        "@attribute",
        "@layout",
        "@namespace",
        "@typeparam",
        "@rendermode",
        "@preservewhitespace",
        "@model",
        "@addTagHelper",
        "@removeTagHelper",
        "@tagHelperPrefix",
        "@code",
        "@functions",
    ];

    /// <summary>A complete tag, so attributes are read only where attributes live.</summary>
    private static readonly Regex Tag = new(
        @"<[^>]*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>An attribute and its double-quoted value.</summary>
    private static readonly Regex Attribute = new(
        @"(?<name>[A-Za-z-]+)\s*=\s*""(?<value>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void There_are_razor_components_to_scan()
    {
        // Guards the scan below: an empty file set makes it vacuously true.
        Assert.NotEmpty(ComponentFiles());
    }

    [Fact]
    public void No_component_renders_a_literal_Latin_word()
    {
        var offenders = new List<string>();

        foreach (var path in ComponentFiles())
        {
            var markup = StripNonMarkup(File.ReadAllText(path));

            foreach (Match node in TextNode.Matches(markup))
            {
                var text = Strip(node.Groups[1].Value);

                if (LatinWord.IsMatch(text))
                {
                    offenders.Add($"{Path.GetFileName(path)}: {text.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_component_renders_a_literal_Latin_word_in_a_user_facing_attribute()
    {
        // NavMenu.razor's title="Navigation menu" was one of the six leaks this story fixed by hand,
        // and it lives in an attribute - invisible to a scan that only reads text nodes. A tooltip is
        // as user-facing as a paragraph, so the same rule and the same allowlist apply.
        var offenders = new List<string>();

        foreach (var path in ComponentFiles())
        {
            var markup = StripNonMarkup(File.ReadAllText(path));

            foreach (Match tag in Tag.Matches(markup))
            {
                foreach (Match attribute in Attribute.Matches(tag.Value))
                {
                    var name = attribute.Groups["name"].Value;

                    if (!UserFacingAttributes.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var value = Strip(attribute.Groups["value"].Value);

                    if (LatinWord.IsMatch(value))
                    {
                        offenders.Add($"{Path.GetFileName(path)}: {name} = {value.Trim()}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_error_page_no_longer_carries_the_development_mode_block()
    {
        // Scaffold noise, English and dead weight all at once (NFR-6, NFR-14). Deleted rather than
        // translated - nobody wants a Ukrainian explanation of the ASPNETCORE_ENVIRONMENT variable.
        var errorPage = File.ReadAllText(Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Web"),
            "Components",
            "Pages",
            "Error.razor"));

        Assert.DoesNotContain("Development Mode", errorPage, StringComparison.Ordinal);
        Assert.DoesNotContain("ASPNETCORE_ENVIRONMENT", errorPage, StringComparison.Ordinal);
    }

    private static string[] ComponentFiles() =>
        Directory.GetFiles(
            Path.Combine(RepositoryLayout.ProjectDirectory("DriveTrack.Web"), "Components"),
            "*.razor",
            SearchOption.AllDirectories);

    /// <summary>
    /// Removes everything that is not rendered markup: comments, <c>@code</c> and <c>@{}</c> blocks,
    /// and directive lines. C# inside a component is code, and prose explaining a rule is not a
    /// breach of it.
    /// </summary>
    private static string StripNonMarkup(string source)
    {
        source = Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));
        source = Regex.Replace(source, @"<!--.*?-->", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5));
        source = RemoveBraceBlocks(source, "@code");
        source = RemoveBraceBlocks(source, "@{");

        var lines = source.Split('\n').Where(line => !IsDirectiveLine(line));

        return string.Join('\n', lines);
    }

    /// <summary>
    /// True for a line that is a Razor directive and nothing else. The name has to end at the
    /// directive - <c>@using</c> is one, <c>@usingTheThing</c> is an expression - or the check would
    /// swallow markup by prefix.
    /// </summary>
    private static bool IsDirectiveLine(string line)
    {
        var trimmed = line.TrimStart();

        return Array.Exists(
            Directives,
            directive => trimmed.StartsWith(directive, StringComparison.Ordinal)
                && (trimmed.Length == directive.Length
                    || !char.IsLetterOrDigit(trimmed[directive.Length])));
    }

    /// <summary>Removes a <c>@code { … }</c> or <c>@{ … }</c> block, braces balanced.</summary>
    private static string RemoveBraceBlocks(string source, string opener)
    {
        var start = source.IndexOf(opener, StringComparison.Ordinal);

        while (start >= 0)
        {
            var brace = source.IndexOf('{', start);

            if (brace < 0)
            {
                break;
            }

            var depth = 0;
            var end = brace;

            for (; end < source.Length; end++)
            {
                if (source[end] == '{')
                {
                    depth++;
                }
                else if (source[end] == '}' && --depth == 0)
                {
                    break;
                }
            }

            if (end >= source.Length)
            {
                break;
            }

            source = source.Remove(start, end - start + 1);
            start = source.IndexOf(opener, StringComparison.Ordinal);
        }

        return source;
    }

    /// <summary>Removes Razor expressions, brace blocks and the allowed brand name from a text node.</summary>
    private static string Strip(string text)
    {
        text = RazorExpression.Replace(text, " ");
        text = Regex.Replace(text, @"[{}]", " ", RegexOptions.None, TimeSpan.FromSeconds(5));

        return AllowedWords.Aggregate(
            text,
            (current, word) => current.Replace(word, " ", StringComparison.Ordinal));
    }
}
