using System.Text.RegularExpressions;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Components;

/// <summary>
/// Shared readers for the component suite: where the shared components live on disk, and how to
/// read a claim out of rendered HTML.
/// <para>
/// Collected here rather than repeated per class because every one of these is a place a test could
/// quietly stop asserting anything — a path that no longer resolves, a regex that stops matching —
/// and one copy is one thing to keep honest.
/// </para>
/// </summary>
internal static class SharedMarkup
{
    /// <summary>Three or more consecutive Latin letters: a word rather than an artefact.</summary>
    private static readonly Regex LatinWord = new(
        @"[A-Za-z]{3,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex Tag = new(
        @"<[^>]*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>The Web project's <c>Components/</c> directory.</summary>
    public static string ComponentsDirectory { get; } = Path.Combine(
        RepositoryLayout.ProjectDirectory("DriveTrack.Web"), "Components");

    /// <summary>The <c>Components/Shared/</c> directory the story's vocabulary lives in.</summary>
    public static string SharedDirectory { get; } = Path.Combine(ComponentsDirectory, "Shared");

    /// <summary>A file under <c>Components/Shared/</c>, read whole.</summary>
    public static string ReadShared(string fileName) =>
        File.ReadAllText(Path.Combine(SharedDirectory, fileName));

    /// <summary>A file under <c>Components/</c>, read whole.</summary>
    public static string ReadComponent(params string[] segments) =>
        File.ReadAllText(Path.Combine([ComponentsDirectory, .. segments]));

    /// <summary>Every file under <c>Components/Shared/</c> that is a component rather than a stylesheet.</summary>
    public static string[] SharedComponents() =>
    [
        .. Directory
            .GetFiles(SharedDirectory, "*", SearchOption.AllDirectories)
            // Filtered by suffix rather than by glob: on Windows the search pattern is not the
            // only thing that decides, and a `*.razor` pattern is one platform quirk away from
            // handing scoped stylesheets to a markup reader.
            .Where(path => path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>How many times <paramref name="needle"/> occurs in <paramref name="haystack"/>.</summary>
    public static int Occurrences(string haystack, string needle)
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

    /// <summary>The text of a fragment of HTML, with every tag removed.</summary>
    public static string TextOf(string html) => Tag.Replace(html, " ").Trim();

    /// <summary>True when the text carries at least one Cyrillic character: it was localized.</summary>
    public static bool IsUkrainian(string text) => text.Any(character => character is >= 'Ѐ' and <= 'ӿ');

    /// <summary>True when the text carries a run of Latin letters: it was not.</summary>
    public static bool HasLatinWord(string text) => LatinWord.IsMatch(text);

    /// <summary>The inner HTML of the first element carrying <paramref name="className"/>.</summary>
    public static string ElementWithClass(string html, string tag, string className)
    {
        var match = Regex.Match(
            html,
            $"<{tag}[^>]*\\b{Regex.Escape(className)}\\b[^>]*>(?<body>.*?)</{tag}>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"No <{tag}> carrying '{className}' in:{Environment.NewLine}{html}");

        return match.Groups["body"].Value;
    }
}
