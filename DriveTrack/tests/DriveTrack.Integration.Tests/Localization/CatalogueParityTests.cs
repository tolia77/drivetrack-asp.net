using System.Globalization;
using System.Resources;
using System.Xml.Linq;
using DriveTrack.Application;
using DriveTrack.Integration.Tests.Persistence;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace DriveTrack.Integration.Tests.Localization;

/// <summary>
/// The two catalogues hold the same keys, and the mail holds only one.
/// <para>
/// Resource fallback is silent by design: a key missing from an English satellite resolves to the
/// neutral Ukrainian value, renders perfectly and says nothing. That is the right behaviour at run
/// time - a half-translated screen beats a screen full of raw identifiers - and it is exactly why
/// the miss has to be caught here instead. The reverse direction matters for the opposite reason: a
/// key that exists only in English is either a dead entry or, worse, a key a component now asks for
/// that a Ukrainian reader will never see translated.
/// </para>
/// <para>
/// The last two tests pin an <em>absence</em>. <c>EmailText</c> has no English satellite, and that
/// is the whole mechanism keeping a client's mail Ukrainian whatever culture the worker thread
/// happens to be in. An absence cannot be read from the code that relies on it, so it is asserted:
/// a stray <c>EmailText.en.resx</c> would silently change what every client receives.
/// </para>
/// </summary>
public class CatalogueParityTests(PostgresFixture postgres)
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Each neutral catalogue and the satellite that has to match it, key for key.</summary>
    public static TheoryData<string, string> Catalogues() => new()
    {
        { "UiText.resx", "UiText.en.resx" },
        { "ErrorMessages.resx", "ErrorMessages.en.resx" },
        { "FieldNames.resx", "FieldNames.en.resx" },
    };

    [Theory]
    [MemberData(nameof(Catalogues))]
    public void Every_key_in_a_neutral_catalogue_has_an_English_counterpart(string neutral, string english)
    {
        var missing = KeysOf(neutral)
            .Except(KeysOf(english), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"{english} is missing {missing.Length} key(s) the neutral catalogue has, which would "
                + $"render in Ukrainian to an English reader: {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(Catalogues))]
    public void Every_key_in_an_English_catalogue_has_a_neutral_counterpart(string neutral, string english)
    {
        var orphans = KeysOf(english)
            .Except(KeysOf(neutral), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            orphans.Length == 0,
            $"{english} holds {orphans.Length} key(s) the neutral catalogue does not, so either the "
                + $"entry is dead or the Ukrainian translation was never written: {string.Join(", ", orphans)}");
    }

    [Theory]
    [MemberData(nameof(Catalogues))]
    public void No_catalogue_holds_the_same_key_twice(string neutral, string english)
    {
        // The claim the two directions above rest on: they compare sets, and a set is where a
        // duplicate goes to hide. Two entries under one name is a silent last-one-wins.
        foreach (var file in new[] { neutral, english })
        {
            var names = Entries(file).Select(entry => entry.Key).ToArray();

            Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public async Task Every_UiText_key_resolves_to_its_English_value_under_an_English_culture()
    {
        // Source parity says the two files agree. It says nothing about whether the satellite
        // assembly is built, copied beside the host and found at run time - and that is a build
        // property away from being false, with every assertion above still green. So this asks the
        // real localizer, resolved out of the real host, under en-US.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);

        using var scope = factory.Services.CreateScope();
        var localizer = scope.ServiceProvider.GetRequiredService<IStringLocalizer<UiText>>();

        var expected = Entries("UiText.en.resx")
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

        Assert.NotEmpty(expected);

        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = English;

        try
        {
            var wrong = expected
                .Where(entry => !string.Equals(localizer[entry.Key].Value, entry.Value, StringComparison.Ordinal))
                .Select(entry => entry.Key)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.True(
                wrong.Length == 0,
                "These keys did not resolve to the English catalogue's value under en-US, so the "
                    + $"satellite assembly is not reaching the host: {string.Join(", ", wrong)}");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public async Task A_key_with_no_translation_falls_back_to_its_Ukrainian_value_rather_than_its_own_name()
    {
        // The matrix row for a key the English catalogue does not carry. It cannot be proven by
        // deleting one, because the two parity tests above exist precisely to stop such a key ever
        // existing - so this asks the same question of a culture that has no satellite at all.
        // fr-FR misses the satellite lookup exactly as a hole in the English catalogue would, and
        // what the chain lands on either way is the neutral file.
        //
        // ResourceNotFound is the half that matters. A miss that ran off the end of the chain hands
        // back the key itself, and "NotFoundTitle" rendered as a page heading is the failure this
        // pins. Ukrainian text is the right answer to a missing translation; the identifier never is.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = await ApiFactory.CreateAsync(postgres.ConnectionString, cancellationToken);

        using var scope = factory.Services.CreateScope();
        var localizer = scope.ServiceProvider.GetRequiredService<IStringLocalizer<UiText>>();

        var neutral = Entries("UiText.resx");

        Assert.NotEmpty(neutral);

        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");

        try
        {
            foreach (var (key, value) in neutral)
            {
                var resolved = localizer[key];

                Assert.False(
                    resolved.ResourceNotFound,
                    $"UiText.{key} ran off the end of the fallback chain and would render as its own name.");

                Assert.Equal(value, resolved.Value);
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void The_mail_catalogue_ships_no_English_satellite()
    {
        // Deliberate, and the reason it is a test rather than a comment: EmailText.en.resx would be
        // four keystrokes and an entirely reasonable-looking commit, and nothing else in the suite
        // would notice a client's notice arriving in a language they were never asked about.
        var resources = new DirectoryInfo(Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Application"),
            "Resources"));

        var satellites = resources
            .GetFiles("EmailText.*.resx")
            .Select(file => file.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            satellites.Length == 0,
            "The mail is Ukrainian for every recipient, and the absence of a satellite is what "
                + $"makes it so: {string.Join(", ", satellites)}");
    }

    [Fact]
    public void The_mail_resolves_Ukrainian_under_an_English_ambient_culture()
    {
        // The file scan above says the satellite is not in the tree; this says the mechanism holds
        // at run time, on the one path where it is actually load-bearing. EmailText reads
        // CultureInfo.CurrentUICulture, and the side-effect worker composes a notice on its own
        // thread - so the question "what happens when that thread is in en-US" has to have an
        // answer, and the answer is the neutral catalogue.
        var resources = new ResourceManager(
            "DriveTrack.Application.Resources.EmailText",
            ApplicationAssembly.Assembly);

        var expected = EmailEntries();

        Assert.NotEmpty(expected);

        // The culture is set on the thread and the lookup is made with no culture argument, because
        // the ambient path is the one that exists in production: EmailText.Value calls
        // GetString(key, CultureInfo.CurrentUICulture) and nothing hands it a culture. Passing
        // en-US explicitly here would exercise a call this system never makes.
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = English;

        try
        {
            foreach (var (key, value) in expected)
            {
                var resolved = resources.GetString(key);

                Assert.Equal(value, resolved);

                Assert.True(
                    resolved!.Any(character => character is >= 'Ѐ' and <= 'ӿ'),
                    $"EmailText.{key} resolved to '{resolved}' under en-US, which is not Ukrainian.");
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static HashSet<string> KeysOf(string fileName) =>
        Entries(fileName).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);

    private static (string Key, string Value)[] Entries(string fileName) =>
        Read(Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Web"),
            "Resources",
            fileName));

    private static (string Key, string Value)[] EmailEntries() =>
        Read(Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Application"),
            "Resources",
            "EmailText.resx"));

    private static (string Key, string Value)[] Read(string path) =>
        XDocument.Load(path)
            .Root!
            .Elements("data")
            .Select(element => (
                Key: element.Attribute("name")?.Value ?? string.Empty,
                Value: element.Element("value")?.Value ?? string.Empty))
            .ToArray();
}
