using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Xml.Linq;
using DriveTrack.Application;
using DriveTrack.Application.Common;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Api;

namespace DriveTrack.Integration.Tests.Contract;

/// <summary>
/// The exhaustiveness gates that make the failure vocabulary genuinely closed rather than closed by
/// convention (AD-8, AD-18).
/// <para>
/// Every rule here is one an eventual ninth code or sixth exception type would break silently: a
/// code with no resource key ships an untranslated identifier to a user, a code with no status arm
/// answers 500 for something that meant 409, and a sixth exception type is a failure the adapter
/// never learns to shape. Each is caught here at build time instead of in Epic 6.
/// </para>
/// </summary>
public class ErrorContractTests
{
    /// <summary>The resource catalogue AD-18 pairs with the enum, read as a file rather than through the runtime.</summary>
    private static readonly string ErrorMessagesResx = Path.Combine(
        RepositoryLayout.ProjectDirectory("DriveTrack.Web"),
        "Resources",
        "ErrorMessages.resx");

    /// <summary>Every declared member of the closed enum.</summary>
    private static readonly ErrorCode[] Codes = Enum.GetValues<ErrorCode>();

    /// <summary>The capability prefixes minted so far. A new capability adds one here deliberately.</summary>
    private static readonly string[] KnownPrefixes =
        ["COMMON_", "AUTH_", "FLEET_", "PERSISTENCE_", "DELIVERY_"];

    /// <summary>AD-8's closed failure set, by name.</summary>
    private static readonly string[] ExpectedExceptionTypes =
    [
        nameof(ConflictException),
        nameof(DomainRuleException),
        nameof(ForbiddenException),
        nameof(NotFoundException),
        nameof(ValidationException),
    ];

    [Fact]
    public void Every_error_code_has_a_resource_key()
    {
        // AD-18. Without this a new code reaches a user as its own member name.
        var keys = ResourceKeys();

        var missing = Codes
            .Select(code => code.ToString())
            .Where(name => !keys.Contains(name))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_resource_key_is_an_error_code()
    {
        // The other direction, and the reason UiText.resx exists as a separate file: a chrome string
        // parked in the error catalogue is dead weight (NFR-6) and quietly weakens the pairing above.
        var names = Codes.Select(code => code.ToString()).ToHashSet(StringComparer.Ordinal);

        var strays = ResourceKeys().Where(key => !names.Contains(key)).Order().ToArray();

        Assert.Empty(strays);
    }

    [Fact]
    public void Every_error_code_is_screaming_snake_under_a_known_capability_prefix()
    {
        var offenders = Codes
            .Select(code => code.ToString())
            .Where(name => !IsScreamingSnake(name)
                || !KnownPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_failure_set_is_exactly_the_five_AD8_types()
    {
        // Found by reflection, not by a list of files: a sixth subclass added anywhere in the
        // Application assembly shows up here even if it was never mentioned in a review.
        var found = ApplicationAssembly.Assembly
            .GetTypes()
            .Where(type => typeof(DriveTrackException).IsAssignableFrom(type))
            .Where(type => !type.IsAbstract)
            .Select(type => type.Name)
            .Order()
            .ToArray();

        Assert.Equal(ExpectedExceptionTypes.Order().ToArray(), found);
    }

    [Fact]
    public void The_failure_set_shares_one_base_that_carries_a_code()
    {
        // The shared base is what makes the status map total: the adapter switches on the code and
        // never on the exception type, so no subclass can introduce a status the map does not cover.
        Assert.True(typeof(DriveTrackException).IsAbstract);

        var code = typeof(DriveTrackException).GetProperty(
            nameof(DriveTrackException.Code),
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(code);
        Assert.Equal(typeof(ErrorCode), code.PropertyType);
    }

    [Fact]
    public void Every_error_code_has_a_status()
    {
        // AD-7's map is total. A member with no arm throws rather than returning a plausible 500,
        // which is the difference between a failing build and a capability that looks fine in
        // review and answers the wrong status in production.
        var offenders = new List<string>();

        foreach (var code in Codes)
        {
            try
            {
                var status = ErrorContract.StatusFor(code);

                if (status is < 400 or > 599)
                {
                    offenders.Add($"{code} -> {status}");
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                offenders.Add($"{code} -> unmapped");
            }
        }

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData(ErrorCode.COMMON_UNEXPECTED_ERROR, 500)]
    [InlineData(ErrorCode.COMMON_NOT_FOUND, 404)]
    [InlineData(ErrorCode.COMMON_VALIDATION_FAILED, 422)]
    [InlineData(ErrorCode.COMMON_CONFLICT, 409)]
    [InlineData(ErrorCode.AUTH_UNAUTHENTICATED, 401)]
    [InlineData(ErrorCode.AUTH_FORBIDDEN, 403)]
    [InlineData(ErrorCode.PERSISTENCE_UNIQUE_VIOLATION, 409)]
    [InlineData(ErrorCode.PERSISTENCE_CHECK_VIOLATION, 422)]
    public void The_status_map_is_the_one_AD7_and_AD8_agree_on(ErrorCode code, int expected)
    {
        // AD-7:173 folds every constraint violation into 409; AD-8:185-186 splits unique from check.
        // AD-8 is the more specific rule and the one the epic cites, so a unique violation is 409
        // and a check violation is 422 - which is also what keeps NFR-2 honest, since a rating of 6
        // then returns 422 whether the validator or the database caught it.
        Assert.Equal(expected, ErrorContract.StatusFor(code));
    }

    [Theory]
    [InlineData(401, ErrorCode.AUTH_UNAUTHENTICATED)]
    [InlineData(403, ErrorCode.AUTH_FORBIDDEN)]
    [InlineData(404, ErrorCode.COMMON_NOT_FOUND)]
    [InlineData(409, ErrorCode.COMMON_CONFLICT)]
    [InlineData(400, ErrorCode.COMMON_VALIDATION_FAILED)]
    [InlineData(422, ErrorCode.COMMON_VALIDATION_FAILED)]
    [InlineData(415, ErrorCode.COMMON_UNEXPECTED_ERROR)]
    [InlineData(503, ErrorCode.COMMON_UNEXPECTED_ERROR)]
    public void The_default_code_map_names_the_failure_a_framework_status_stands_for(
        int statusCode,
        ErrorCode expected)
    {
        // The other direction of AD-7's table. The HTTP suite reaches only the 404 arm, because
        // Epic 1 ships no action returning Conflict() and no scheme whose authorization
        // short-circuit the backstop would have to name - so swapping the 401 and 403 arms left the
        // whole suite green until this table existed. Both callers, the result filter's client-error
        // arm and the middleware's body-less backstop, decide the client's code from here.
        Assert.Equal(expected, ErrorContract.DefaultCodeFor(statusCode));
    }

    [Fact]
    public void The_resource_catalogue_is_reachable_through_the_resource_manager()
    {
        // The file assertions above prove the .resx is right; this proves it was embedded under the
        // name IStringLocalizer<ErrorMessages> will look for. Getting the folder or the namespace
        // wrong fails nothing at build time and returns the key itself at run time.
        var manager = new ResourceManager(
            "DriveTrack.Web.Resources.ErrorMessages",
            typeof(Web.Resources.ErrorMessages).Assembly);

        var missing = Codes
            .Where(code => manager.GetString(code.ToString(), CultureInfo.GetCultureInfo("uk-UA")) is null)
            .Select(code => code.ToString())
            .ToArray();

        Assert.Empty(missing);
    }

    private static HashSet<string> ResourceKeys() =>
        XDocument.Load(ErrorMessagesResx)
            .Root!
            .Elements("data")
            .Select(element => element.Attribute("name")?.Value ?? string.Empty)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.Ordinal);

    private static bool IsScreamingSnake(string name) =>
        name.Length > 0
        && name.All(character => (character >= 'A' && character <= 'Z') || character == '_')
        && !name.StartsWith('_')
        && !name.EndsWith('_');
}
