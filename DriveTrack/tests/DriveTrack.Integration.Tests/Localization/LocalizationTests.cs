using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using DriveTrack.Application.Common;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace DriveTrack.Integration.Tests.Localization;

/// <summary>
/// NFR-14 and NFR-15: the interface is Ukrainian and the locale is not a preference.
/// <para>
/// The product rule is that the UI <em>is</em> Ukrainian, so the culture is stated rather than
/// negotiated - the provider chain is cleared, not merely narrowed. With one supported culture a
/// negotiated result would be uk-UA anyway; clearing it is what stops a future second culture being
/// selected by a header nobody decided to honour.
/// </para>
/// </summary>
public class LocalizationTests(PostgresFixture postgres)
{
    private static readonly CultureInfo Ukrainian = CultureInfo.GetCultureInfo("uk-UA");

    private static readonly string[] ResourceFiles = ["ErrorMessages.resx", "UiText.resx"];

    [Fact]
    public async Task An_Accept_Language_header_cannot_negotiate_the_culture_away()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/api/probe/culture", UriKind.Relative));
        request.Headers.Add("Accept-Language", "en-US");

        using var response = await client.SendAsync(request, cancellationToken);
        var envelope = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);

        Assert.NotNull(envelope);

        var data = envelope.RootElement.GetProperty("data");

        Assert.Equal("uk-UA", data.GetProperty("culture").GetString());
        Assert.Equal("uk-UA", data.GetProperty("uiCulture").GetString());
    }

    [Fact]
    public async Task Every_error_code_resolves_through_the_localizer()
    {
        // ResourceNotFound is the failure that has no other symptom: IStringLocalizer returns the
        // key itself, so a misplaced resx ships identifiers to users and nothing else goes wrong.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var localizer = scope.ServiceProvider.GetRequiredService<IStringLocalizer<ErrorMessages>>();

        var offenders = Enum.GetValues<ErrorCode>()
            .Select(code => localizer[code.ToString()])
            .Where(localized => localized.ResourceNotFound)
            .Select(localized => localized.Name)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("ErrorMessages.resx")]
    [InlineData("UiText.resx")]
    public void Every_resource_value_is_written_in_Ukrainian(string fileName)
    {
        // "Has a resource key" and "the key holds a translation" are different claims, and the
        // second is the one NFR-14 makes. An untranslated placeholder passes every other test here.
        var offenders = ValuesOf(fileName)
            .Where(entry => !entry.Value.Any(IsCyrillic))
            .Select(entry => entry.Key)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_resource_value_carries_an_English_word()
    {
        // A run of Latin letters in a translated string is either an untranslated fragment or a
        // leaked key. Neither belongs in front of a user (NFR-14).
        var offenders = ResourceFiles
            .SelectMany(file => ValuesOf(file).Select(entry => (File: file, entry.Key, entry.Value)))
            .Where(entry => HasLatinWord(entry.Value))
            .Select(entry => $"{entry.File}:{entry.Key}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_Ukrainian_locale_formats_dates_and_numbers_its_own_way()
    {
        // NFR-15. Asserted against a known instant and a known number rather than against
        // CurrentCulture, so this fails on a machine whose ICU data is missing rather than passing
        // because the invariant culture happened to be in scope.
        var date = new DateOnly(2026, 9, 7);

        Assert.Equal("07.09.2026", date.ToString("d", Ukrainian));

        // A non-breaking space as the group separator and a comma as the decimal mark.
        var formatted = 1234.5m.ToString("N1", Ukrainian);

        Assert.Contains(",", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(".", formatted, StringComparison.Ordinal);
        Assert.StartsWith("1", formatted, StringComparison.Ordinal);
        Assert.EndsWith("234,5", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void The_runtime_image_installs_the_full_ICU_data_set()
    {
        // Every other assertion in this suite reads the developer machine's ICU, so none of them can
        // see the one environment NFR-15 is actually promised in. On Alpine, icu-libs alone pulls
        // only icu-data-en: uk-UA then *resolves* while formatting 9/7/2026 and 1,234.5, which is a
        // silent breach that starts up cleanly and passes every test here. icu-data-full is the fix,
        // and this scan is the only thing that can notice it being dropped.
        var dockerfile = File.ReadAllLines(
            Path.Combine(RepositoryLayout.SolutionRoot.FullName, "Dockerfile"));

        // Every apk line, not the first: a later story adding one to the build stage would otherwise
        // hand this assertion an unrelated line and fail it for an unrelated reason, which is the
        // kind of failure people fix by relaxing the assertion.
        var apkLines = Array.FindAll(
            dockerfile,
            line => line.TrimStart().StartsWith("RUN apk add", StringComparison.Ordinal));

        Assert.NotEmpty(apkLines);
        Assert.Contains(apkLines, line => line.Contains("icu-libs", StringComparison.Ordinal));
        Assert.Contains(apkLines, line => line.Contains("icu-data-full", StringComparison.Ordinal));

        // The packages are half of it. Invariant mode ignores every byte of ICU on disk, so the
        // switch that turns it off is as load-bearing as the install - and it lives on a
        // backslash-continued ENV line, where a routine edit can drop it without touching the apk
        // line above.
        Assert.Contains(
            dockerfile,
            line => line.Contains(
                "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_key_the_components_ask_for_resolves()
    {
        // The UiText twin of the error-code test above. A renamed key, a moved resx or a
        // ResourcesPath added to AddLocalization all have the same symptom - IStringLocalizer hands
        // back the key itself - and no other test in the suite would notice, because nothing else
        // resolves UiText through a localizer at all.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var localizer = scope.ServiceProvider.GetRequiredService<IStringLocalizer<UiText>>();

        var used = KeysUsedByComponents();

        Assert.NotEmpty(used);

        var unresolved = used
            .Where(key => localizer[key].ResourceNotFound)
            .Order()
            .ToArray();

        Assert.Empty(unresolved);
    }

    [Fact]
    public void Every_UiText_key_is_asked_for_by_a_component()
    {
        // The reverse direction. A key no component reads is dead weight (NFR-6) and, worse, makes
        // the forward test above look more thorough than it is.
        var used = KeysUsedByComponents();

        var unused = ValuesOf("UiText.resx")
            .Select(entry => entry.Key)
            .Where(key => !used.Contains(key))
            .Order()
            .ToArray();

        Assert.Empty(unused);
    }

    /// <summary>Every <c>@Localizer["Key"]</c> a component under <c>Components/</c> asks for.</summary>
    private static HashSet<string> KeysUsedByComponents()
    {
        var components = Directory.GetFiles(
            Path.Combine(RepositoryLayout.ProjectDirectory("DriveTrack.Web"), "Components"),
            "*.razor",
            SearchOption.AllDirectories);

        var pattern = new System.Text.RegularExpressions.Regex(
            @"@Localizer\[""([^""]+)""\]",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        return components
            .SelectMany(path => pattern.Matches(File.ReadAllText(path)).Cast<System.Text.RegularExpressions.Match>())
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<(string Key, string Value)> ValuesOf(string fileName)
    {
        var path = Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Web"),
            "Resources",
            fileName);

        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .Select(element => (
                Key: element.Attribute("name")?.Value ?? string.Empty,
                Value: element.Element("value")?.Value ?? string.Empty))
            .ToArray();
    }

    private static bool IsCyrillic(char character) => character is >= 'Ѐ' and <= 'ӿ';

    private static bool HasLatinWord(string value)
    {
        var run = 0;

        foreach (var character in value)
        {
            run = char.IsAsciiLetter(character) ? run + 1 : 0;

            if (run >= 3)
            {
                return true;
            }
        }

        return false;
    }
}
