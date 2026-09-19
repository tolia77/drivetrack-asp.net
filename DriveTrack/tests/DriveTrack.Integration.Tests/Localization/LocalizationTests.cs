using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace DriveTrack.Integration.Tests.Localization;

/// <summary>
/// NFR-14 and NFR-15: the interface is Ukrainian or English, and which one is the user's decision
/// rather than their browser's.
/// <para>
/// Ukrainian is the baseline and the default - it is what the neutral catalogues hold and what a
/// visitor who has chosen nothing is served. English ships as a satellite beside each neutral file.
/// The culture is still not negotiated: the provider chain holds
/// <c>CookieRequestCultureProvider</c> and nothing else, so an <c>Accept-Language</c> header or a
/// query string cannot select a language. Only the POST to <c>/set-culture</c> can.
/// </para>
/// <para>
/// Every guard below that used to read "this catalogue is Ukrainian" now reads "this catalogue is
/// in its own language", in both directions: Ukrainian prose in an English file fails, and English
/// prose in a Ukrainian one still fails. None was dropped - a catalogue that lost its language
/// would otherwise do it silently, because a mixed-language screen renders perfectly.
/// </para>
/// </summary>
public class LocalizationTests(PostgresFixture postgres)
{
    private static readonly CultureInfo Ukrainian = CultureInfo.GetCultureInfo("uk-UA");

    /// <summary>The neutral catalogues, which are Ukrainian (see <c>NeutralResourcesLanguage</c>).</summary>
    private static readonly string[] ResourceFiles =
        ["ErrorMessages.resx", "FieldNames.resx", "UiText.resx"];

    /// <summary>The English satellites, one beside each neutral file.</summary>
    private static readonly string[] EnglishResourceFiles =
        ["ErrorMessages.en.resx", "FieldNames.en.resx", "UiText.en.resx"];

    /// <summary>
    /// The two keys exempt from the per-catalogue language guards, in both directions.
    /// <para>
    /// The language switcher names each language as its own speakers write it - Українська and
    /// English - and both catalogues carry the same two words, because a reader who cannot read the
    /// page they are on has to be able to find their own language on it. "Англійська" is invisible
    /// to the English reader the control exists for, and "Ukrainian" is invisible to the Ukrainian
    /// one. So the guards treat these two as proper nouns, exactly as
    /// <see cref="UserFacingTextTests"/> treats the brand name, and for the same reason: a name in
    /// its own script is not an untranslated string.
    /// </para>
    /// <para>
    /// Kept to exactly two, so a third arrival is a deliberate decision rather than a habit.
    /// </para>
    /// </summary>
    private static readonly string[] EndonymKeys = ["LanguageUkrainian", "LanguageEnglish"];

    /// <summary>The English satellites, one per theory case.</summary>
    public static TheoryData<string> EnglishCatalogues() => [.. EnglishResourceFiles];

    [Fact]
    public async Task The_culture_cookie_selects_the_language_and_an_Accept_Language_header_still_does_not()
    {
        // The two halves of the rule in one test, because separately either is misleading: "the
        // cookie works" is satisfied by a chain that honours everything, and "the header is ignored"
        // is satisfied by the cleared chain this replaced, which honoured nothing and made the
        // second language unreachable.
        //
        // The header is sent on BOTH requests, so the second assertion is not merely "no cookie
        // means Ukrainian" - it is "a browser asking for English in the documented way is still
        // served Ukrainian", which is what keeps the choice a deliberate act rather than a guess.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);
        using var client = factory.CreateClient();

        using (var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/api/probe/culture", UriKind.Relative)))
        {
            request.Headers.Add("Accept-Language", "en-US");

            using var response = await client.SendAsync(request, cancellationToken);
            var envelope = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);

            Assert.NotNull(envelope);

            var data = envelope.RootElement.GetProperty("data");

            Assert.Equal("uk-UA", data.GetProperty("culture").GetString());
            Assert.Equal("uk-UA", data.GetProperty("uiCulture").GetString());
        }

        using (var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/api/probe/culture?culture=en-US&ui-culture=en-US", UriKind.Relative)))
        {
            // The other provider the framework chain carries by default, and the one the class doc
            // and Program.cs both claim cannot select a language. Spelled with the exact parameter
            // names QueryStringRequestCultureProvider reads, so this fails the day somebody adds it
            // back rather than passing because the query string was misspelled.
            using var response = await client.SendAsync(request, cancellationToken);
            var envelope = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);

            Assert.NotNull(envelope);

            var data = envelope.RootElement.GetProperty("data");

            Assert.Equal("uk-UA", data.GetProperty("culture").GetString());
            Assert.Equal("uk-UA", data.GetProperty("uiCulture").GetString());
        }

        using (var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/api/probe/culture", UriKind.Relative)))
        {
            // Ukrainian in the header and English in the cookie, so the two cannot both be
            // satisfied by the same answer: only the cookie may win.
            request.Headers.Add("Accept-Language", "uk-UA");
            request.Headers.Add("Cookie", CultureCookie.Header("en-US"));

            using var response = await client.SendAsync(request, cancellationToken);
            var envelope = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);

            Assert.NotNull(envelope);

            var data = envelope.RootElement.GetProperty("data");

            Assert.Equal("en-US", data.GetProperty("culture").GetString());
            Assert.Equal("en-US", data.GetProperty("uiCulture").GetString());
        }
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
    [InlineData("FieldNames.resx")]
    [InlineData("UiText.resx")]
    public void Every_resource_value_is_written_in_Ukrainian(string fileName)
    {
        // "Has a resource key" and "the key holds a translation" are different claims, and the
        // second is the one NFR-14 makes. An untranslated placeholder passes every other test here.
        var offenders = Guarded(fileName)
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
            .SelectMany(file => Guarded(file).Select(entry => (File: file, entry.Key, entry.Value)))
            .Where(entry => HasLatinWord(entry.Value))
            .Select(entry => $"{entry.File}:{entry.Key}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Theory]
    [MemberData(nameof(EnglishCatalogues))]
    public void Every_English_resource_value_is_written_in_English(string fileName)
    {
        // The mirror of the Ukrainian guard above, and it earns its place for the same reason: a
        // copied-over Ukrainian value resolves, renders and reads as a finished translation to
        // anyone who does not speak the language it is actually in.
        var offenders = Guarded(fileName)
            .Where(entry => !entry.Value.Any(char.IsAsciiLetter))
            .Select(entry => entry.Key)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_English_resource_value_carries_a_Cyrillic_letter()
    {
        // The other direction, mirroring No_resource_value_carries_an_English_word - and stricter
        // than that one has to be, deliberately. A single Cyrillic letter is enough: `Ні` and `МБ`
        // are two letters each and are exactly the kind of leftover a run-length rule waves through,
        // and `Shipment доставлено` carries Latin letters, so the test above cannot see it either.
        // There is no legitimate Cyrillic in an English catalogue outside the two endonyms.
        var offenders = EnglishResourceFiles
            .SelectMany(file => Guarded(file).Select(entry => (File: file, entry.Key, entry.Value)))
            .Where(entry => entry.Value.Any(IsCyrillic))
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

    [Fact]
    public void Every_delivery_status_has_an_email_label_and_it_matches_the_screens()
    {
        // Story 5.2 puts a second catalogue in the system, and this is the reason that is tolerable.
        // Mail copy cannot live in UiText.resx - that catalogue is closed against the keys components
        // ask for, so a subject line no component renders would fail the reverse test - and
        // DriveTrack.Application carries no IStringLocalizer to read one with. So the four status
        // labels exist twice, and the only thing that can stop them drifting is an assertion that
        // they are the same words: a client reading one wording in an email and another on the
        // screen is one product speaking two languages.
        var ui = ValuesOf("UiText.resx").ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        var email = EmailValues().ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

        foreach (var status in Enum.GetValues<DeliveryStatus>())
        {
            var key = "Status" + status;

            Assert.True(email.ContainsKey(key), $"EmailText.resx has no entry for {key}.");
            Assert.True(ui.ContainsKey(key), $"UiText.resx has no entry for {key}.");
            Assert.Equal(ui[key], email[key]);
        }
    }

    [Fact]
    public void Every_email_template_is_written_in_Ukrainian()
    {
        // NFR-14 reaches the mail as well as the screens: an untranslated subject line is a Latin
        // sentence in front of a client, and it is the one user-facing string no render test can see.
        var offenders = EmailValues()
            .Where(entry => !entry.Value.Any(IsCyrillic) || HasLatinWord(entry.Value))
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_email_catalogue_carries_the_subject_and_body_the_notice_is_built_from()
    {
        // The forward direction, which nothing else covers: a ResourceManager answers a missing key
        // with the key itself, exactly as IStringLocalizer does, so a renamed or moved entry ships
        // an identifier to a client and nothing else goes wrong.
        var keys = EmailValues().Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("StatusChangeSubject", keys);
        Assert.Contains("StatusChangeBody", keys);

        // The placeholders the composer fills. Without them the notice names no delivery, which is
        // the one thing a client can quote back to dispatch.
        var body = EmailValues().Single(entry => entry.Key == "StatusChangeBody").Value;

        Assert.Contains("{0}", body, StringComparison.Ordinal);
        Assert.Contains("{1}", body, StringComparison.Ordinal);
        Assert.Contains("{2}", body, StringComparison.Ordinal);
    }

    /// <summary>The entries of <c>DriveTrack.Application/Resources/EmailText.resx</c>.</summary>
    private static IEnumerable<(string Key, string Value)> EmailValues()
    {
        var path = Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Application"),
            "Resources",
            "EmailText.resx");

        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .Select(element => (
                Key: element.Attribute("name")?.Value ?? string.Empty,
                Value: element.Element("value")?.Value ?? string.Empty))
            .ToArray();
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

    /// <summary>The entries of a catalogue that the language guards judge: everything but the endonyms.</summary>
    private static IEnumerable<(string Key, string Value)> Guarded(string fileName) =>
        ValuesOf(fileName).Where(entry => !EndonymKeys.Contains(entry.Key, StringComparer.Ordinal));

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
