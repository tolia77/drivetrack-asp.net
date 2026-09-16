using System.Globalization;
using System.Xml.Linq;
using DriveTrack.Application.Chat;
using DriveTrack.Application.Common;
using DriveTrack.Application.Deliveries;
using DriveTrack.Application.Drivers;
using DriveTrack.Application.Reviews;
using DriveTrack.Application.Users;
using DriveTrack.Application.Notifications;
using DriveTrack.Application.Shifts;
using DriveTrack.Application.Vehicles;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Reviews;
using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Localization;

/// <summary>
/// A bounded refusal names the bound, and the bound it names is the one the rule applies.
/// <para>
/// This is the half of "specific reasons" a catalogue cannot keep on its own. A message may only be
/// a static string — <c>IStringLocalizer</c> is handed a key and nothing else, and the one place a
/// field message is resolved is the transport, which this change does not touch — so the figure in
/// "максимум 1000 символів" is typed out rather than interpolated. A figure typed out is a figure
/// that goes stale the first time a column widens, and the message then lies in the most useful
/// possible place: it tells a caller exactly how much to cut, wrongly.
/// </para>
/// <para>
/// So the pairing is asserted instead. Each sentence is read from both catalogues and matched
/// against the constant the validator actually uses, in that language's own number format — a
/// comma for the decimal mark in Ukrainian, a point in English (NFR-15). Widening a column now
/// fails here rather than shipping.
/// </para>
/// </summary>
public class BoundedMessageTests
{
    private static readonly CultureInfo Ukrainian = CultureInfo.GetCultureInfo("uk-UA");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// Every message that quotes a limit, beside the constant that limit comes from.
    /// <para>
    /// Written out rather than discovered, because there is nothing to discover from: a resource
    /// value is a string and a validator constant is a number, and the only thing that knows they
    /// are about each other is this table. Its completeness is what the second test guards.
    /// </para>
    /// </summary>
    private static readonly (ErrorCode Code, decimal[] Limits)[] Bounds =
    [
        (ErrorCode.COMMON_PAGING_LIMIT_INVALID, [PageCeiling]),
        (ErrorCode.AUTH_FIRST_NAME_TOO_LONG, [RegisterClientCommandValidator.NameMaximumLength]),
        (ErrorCode.AUTH_LAST_NAME_TOO_LONG, [RegisterClientCommandValidator.NameMaximumLength]),
        (ErrorCode.AUTH_PASSWORD_TOO_LONG, [RegisterClientCommandValidator.PasswordMaximumLength]),
        (ErrorCode.FLEET_MODEL_TOO_LONG, [CreateVehicleCommandValidator.ModelMaximumLength]),
        (ErrorCode.FLEET_LICENSE_PLATE_TOO_LONG, [CreateVehicleCommandValidator.LicensePlateMaximumLength]),
        (ErrorCode.FLEET_CAPACITY_TOO_LARGE, [CreateVehicleCommandValidator.CapacityMaximum]),
        (ErrorCode.FLEET_LICENSE_NUMBER_TOO_LONG, [CreateDriverCommandValidator.LicenseNumberMaximumLength]),
        (ErrorCode.DELIVERY_PACKAGE_DETAILS_TOO_LONG, [CreateDeliveryCommandValidator.PackageDetailsMaximumLength]),
        (ErrorCode.DELIVERY_NOTES_TOO_LONG, [CreateDeliveryCommandValidator.DeliveryNotesMaximumLength]),
        (ErrorCode.DELIVERY_PACKAGE_WEIGHT_TOO_LARGE, [CreateDeliveryCommandValidator.PackageWeightMaximum]),
        (ErrorCode.DELIVERY_NOTE_TOO_LONG, [TimelineEntry.NoteMaximumLength]),
        (ErrorCode.DELIVERY_RECIPIENT_NAME_TOO_LONG, [CaptureProofCommandValidator.RecipientNameMaximumLength]),
        (ErrorCode.DELIVERY_PROOF_TOO_MANY_PHOTOS, [ProofAssetRules.MaximumPhotos]),
        (ErrorCode.DELIVERY_PROOF_ASSET_TOO_LARGE, [ProofAssetRules.MaximumAssetBytes / (1024 * 1024)]),
        (ErrorCode.DELIVERY_PLACE_QUERY_TOO_SHORT, [SearchPlacesQueryValidator.MinimumLength]),
        (ErrorCode.CHAT_MESSAGE_TEXT_TOO_LONG, [SendMessageCommandValidator.TextMaximumLength]),
        (ErrorCode.REVIEW_TEXT_TOO_LONG, [CreateReviewCommandValidator.TextMaximumLength]),
        (ErrorCode.REVIEW_RATING_OUT_OF_RANGE, [RatingScale.Minimum, RatingScale.Maximum]),

        // Declared with no figure, which is a decision rather than an omission. Each of these names
        // its bound in words because the bound is not a number a caller could usefully be told:
        // "більшою за нуль" is the floor, "від'ємним" is the same claim from the other side, and a
        // coordinate's ±90/±180 describe the map rather than anything typed into a box.
        (ErrorCode.FLEET_CAPACITY_NOT_POSITIVE, []),
        (ErrorCode.DELIVERY_PACKAGE_WEIGHT_NOT_POSITIVE, []),
        (ErrorCode.FLEET_MILEAGE_NEGATIVE, []),
        (ErrorCode.COMMON_COORDINATE_OUT_OF_RANGE, []),
    ];

    /// <summary>
    /// The page ceiling the shared paging sentence quotes. Every list validator declares its own,
    /// and <see cref="The_five_list_validators_agree_on_one_page_ceiling"/> is what lets one
    /// sentence speak for all five - without it, pinning the message to one of them would leave the
    /// other four free to drift away from the number their own refusal renders.
    /// </summary>
    private const int PageCeiling = ListDeliveriesQueryValidator.MaximumLimit;

    /// <summary>
    /// The name endings that mark a message as one about a bound - a ceiling, a floor or a range.
    /// A code in one of these families and missing from the table above is a sentence whose figure
    /// nothing is watching.
    /// </summary>
    private static readonly string[] BoundedSuffixes =
        ["_TOO_LONG", "_TOO_LARGE", "_TOO_SHORT", "_OUT_OF_RANGE", "_NOT_POSITIVE", "_NEGATIVE",
         "_PAGING_LIMIT_INVALID"];

    /// <summary>
    /// The one family that is not a suffix. <c>_TOO_MANY_PHOTOS</c> as a suffix would let a later
    /// <c>_TOO_MANY_ASSETS</c> straight through, which is the whole failure this guard is for.
    /// </summary>
    private const string BoundedInfix = "_TOO_MANY_";

    public static TheoryData<ErrorCode, decimal> BoundedMessages()
    {
        var data = new TheoryData<ErrorCode, decimal>();

        foreach (var (code, limits) in Bounds)
        {
            foreach (var limit in limits)
            {
                data.Add(code, limit);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BoundedMessages))]
    public void A_bounded_message_quotes_the_limit_its_rule_applies(ErrorCode code, decimal limit)
    {
        AssertQuotes(Value("ErrorMessages.resx", code), limit, Ukrainian, code);
        AssertQuotes(Value("ErrorMessages.en.resx", code), limit, English, code);
    }

    [Fact]
    public void The_five_list_validators_agree_on_one_page_ceiling()
    {
        // The shared paging sentence names one number for five validators, so the five have to be
        // one number. Pinning the sentence to Deliveries alone would have said nothing about the
        // other four - and a widened ceiling on any of them would then refuse at 200 while telling
        // the caller the limit was 100.
        int[] ceilings =
        [
            ListDeliveriesQueryValidator.MaximumLimit,
            ListVehiclesQueryValidator.MaximumLimit,
            ListShiftsQueryValidator.MaximumLimit,
            ListReviewsQueryValidator.MaximumLimit,
            ListNotificationAttemptsQueryValidator.MaximumLimit,
        ];

        Assert.All(ceilings, ceiling => Assert.Equal(PageCeiling, ceiling));
    }

    [Fact]
    public void Every_message_that_is_about_a_bound_is_paired_with_the_constant_behind_it()
    {
        // The table's completeness, which is the only thing that stops it looking thorough while a
        // new bounded code sits outside it with a figure nobody is checking.
        var declared = Bounds.Select(bound => bound.Code).ToHashSet();

        var unpaired = Enum.GetValues<ErrorCode>()
            .Where(IsAboutABound)
            .Where(code => !declared.Contains(code))
            .Select(code => code.ToString())
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unpaired);
    }

    /// <summary>True for a code whose name puts it in one of the bounded families.</summary>
    private static bool IsAboutABound(ErrorCode code)
    {
        var name = code.ToString();

        return name.Contains(BoundedInfix, StringComparison.Ordinal)
            || BoundedSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Asserts that a sentence quotes a figure as a whole number rather than as a substring of one.
    /// <para>
    /// The boundary is what makes the guard worth having. A plain <c>Contains</c> for 100 is
    /// satisfied by a sentence saying 1000, so a doubled column length would pass the very test
    /// written to catch it; the digit lookarounds are what close that. Only digits are excluded on
    /// either side, not punctuation - a sentence may legitimately end on its figure.
    /// </para>
    /// </summary>
    private static void AssertQuotes(string sentence, decimal limit, CultureInfo culture, ErrorCode code)
    {
        var figure = limit.ToString(culture);

        var quoted = new System.Text.RegularExpressions.Regex(
            @"(?<![0-9])"
                + System.Text.RegularExpressions.Regex.Escape(figure)
                + @"(?![0-9])",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        Assert.True(
            quoted.IsMatch(sentence),
            $"{code} ({culture.Name}) does not quote {figure}: \"{sentence}\"");
    }

    private static string Value(string fileName, ErrorCode code)
    {
        var path = Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Web"),
            "Resources",
            fileName);

        var entry = XDocument.Load(path)
            .Root!
            .Elements("data")
            .SingleOrDefault(element => element.Attribute("name")?.Value == code.ToString());

        Assert.True(entry is not null, $"{fileName} has no entry for {code}.");

        return entry!.Element("value")?.Value ?? string.Empty;
    }
}
