using System.Xml.Linq;
using DriveTrack.Application;
using DriveTrack.Application.Common;
using DriveTrack.Integration.Tests.Support;
using DriveTrack.Web.Account;
using FluentValidation;
using FluentValidation.Validators;
using Microsoft.Extensions.DependencyInjection;
using ValidationException = DriveTrack.Application.Common.ValidationException;

namespace DriveTrack.Integration.Tests.Localization;

/// <summary>
/// <c>FieldNames.resx</c> is the third localized catalogue, and this is what makes a third one
/// tolerable: it is closed in both directions against the field names a validator can actually
/// produce.
/// <para>
/// The two existing catalogues are each closed against something the compiler or another test can
/// see — <c>ErrorMessages</c> against <see cref="ErrorCode"/>, <c>UiText</c> against the literal
/// keys a component asks for. A field label is looked up by a runtime string, so neither closure
/// reaches it, and without these two assertions the catalogue would drift in both directions at
/// once: a rule naming a property nobody translated would put a Latin CLR name in front of a
/// Ukrainian-speaking user, and a label nobody can reach would look like coverage that is not there.
/// </para>
/// </summary>
public class FieldNameTests
{
    /// <summary>
    /// The field names that reach a screen without passing through a validator.
    /// <para>
    /// <c>EfUserAccountRepository</c> turns ASP.NET Identity's own refusals into
    /// <see cref="FieldError"/>s with these two names written out as literals, which the descriptor
    /// walk below cannot see. Listed rather than discovered, because a string literal in a
    /// repository is not something reflection can enumerate.
    /// </para>
    /// <para>
    /// It is not the only producer the walk cannot see: <c>Api/RequestBodyBindingFilter</c> writes
    /// <see cref="FieldError"/>s too. Those are left out on purpose rather than overlooked — the
    /// filter names fields as the <em>wire</em> spells them, for a REST caller reading
    /// <c>error.fields</c>, and that path never reaches a Blazor banner or this catalogue.
    /// </para>
    /// </summary>
    private static readonly string[] NamedOutsideAValidator = ["Email", "Password"];

    /// <summary>
    /// Field labels whose wording a form also shows, and the <c>UiText</c> key the form reads it
    /// from. Written out rather than matched by name, because the pairs that drift are exactly the
    /// ones whose keys differ — and because <c>UiText</c> separately holds <c>Rating</c> for the
    /// driver-score column ("Рейтинг"), which is a different word for a different thing and must
    /// not be compared against the review form's <c>Rating</c> box.
    /// </summary>
    private static readonly Dictionary<string, string> SameWordingAs = new(StringComparer.Ordinal)
    {
        ["Assets"] = "ProofPhotos",
        ["CapacityKg"] = "CapacityKg",
        ["CaptureLocation"] = "ProofLocation",
        ["CurrentPassword"] = "CurrentPassword",
        ["DeliveryNotes"] = "DeliveryNotes",
        ["Dropoff"] = "Dropoff",
        ["Email"] = "Email",
        ["EndedAt"] = "ShiftEndedAt",
        ["FirstName"] = "FirstName",
        ["LastName"] = "LastName",
        ["LicenseNumber"] = "LicenseNumber",
        ["LicensePlate"] = "LicensePlate",
        ["Mileage"] = "Mileage",
        ["Model"] = "Model",
        ["NewPassword"] = "NewPassword",
        ["NewPasswordConfirmation"] = "NewPasswordConfirmation",
        ["Note"] = "TimelineNote",
        ["PackageDetails"] = "PackageDetails",
        ["PackageWeightKg"] = "PackageWeightKg",
        ["Password"] = "Password",
        ["PasswordConfirmation"] = "PasswordConfirmation",
        ["PhoneNumber"] = "PhoneNumber",
        ["Pickup"] = "Pickup",
        ["Query"] = "AddressSearch",
        ["Rating"] = "ReviewRating",
        ["RecipientName"] = "ProofRecipientName",
        ["StartedAt"] = "ShiftStartedAt",
        ["Status"] = "Status",
        ["Text"] = "ReviewText",
        ["WindowLatestAt"] = "WindowLatestAt",
    };

    /// <summary>
    /// The field labels no form shows, so there is no wording for them to agree with. Paging
    /// bounds: a caller can be refused on one, and no screen offers a box for either.
    /// </summary>
    private static readonly string[] NotShownOnAForm = ["Limit", "Offset"];

    [Fact]
    public void Every_field_a_validator_can_name_has_a_label()
    {
        // The forward direction. A validator that names a property with no entry here renders a
        // banner with no label at all - the fallback is safe, but it is silence where the whole
        // point of the change was to say which box was refused (NFR-4).
        var named = FieldsValidatorsCanName();

        Assert.NotEmpty(named);

        var labelled = Labels().Keys;

        var missing = named
            .Where(field => !labelled.Contains(field))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_field_label_is_reachable_from_a_validator()
    {
        // The reverse direction, for the reason the other two catalogues have one: an entry nothing
        // can reach is dead weight (NFR-6) and makes the forward test look more thorough than it is.
        var named = FieldsValidatorsCanName();

        var unreachable = Labels()
            .Keys
            .Where(key => !named.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unreachable);
    }

    [Fact]
    public void A_label_is_looked_up_by_the_root_of_the_path_the_validator_wrote()
    {
        // The seam the two assertions above stand on. A child validator's rule arrives as
        // `Pickup.Latitude` and a collection rule as `Assets[0]`, and the form has a box called
        // neither - so the catalogue is keyed on the root and FailureKeys is what cuts the path
        // down to it. Asserted here rather than assumed, because if the normalization changed
        // shape both directions above would still pass while every nested failure lost its label.
        Assert.Equal("Pickup", Root("Pickup.Latitude"));
        Assert.Equal("Assets", Root("Assets[0]"));
        Assert.Equal("Email", Root("Email"));
    }

    [Fact]
    public void A_label_a_form_also_shows_is_worded_the_way_the_form_words_it()
    {
        // The claim FieldNames.resx's own header makes, and nothing else could keep: a banner that
        // calls a box something the label above it does not is two names for one input. The keys
        // differ on five of these pairs, which is exactly where a rewording drifts unnoticed.
        var fields = Labels();
        var ui = UiTextValues();

        foreach (var (field, uiKey) in SameWordingAs)
        {
            Assert.True(fields.ContainsKey(field), $"FieldNames.resx has no entry for {field}.");
            Assert.True(ui.ContainsKey(uiKey), $"UiText.resx has no entry for {uiKey}.");
            Assert.Equal(ui[uiKey], fields[field]);
        }

        // And the table stays complete: a new label joins it or is declared as one no form shows,
        // rather than quietly being neither.
        var unaccounted = fields.Keys
            .Where(key => !SameWordingAs.ContainsKey(key) && !NotShownOnAForm.Contains(key, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unaccounted);
    }

    /// <summary>
    /// Every field name a validator in the application layer can put on a
    /// <see cref="FieldError"/>, normalized the way a screen normalizes it, plus the two a
    /// repository writes by hand.
    /// </summary>
    private static HashSet<string> FieldsValidatorsCanName()
    {
        // Discovery from AddApplication() rather than from a list: a validator added to the layer
        // is covered by having been written, which is the same rule its registration follows.
        var services = new ServiceCollection();
        services.AddApplication();

        using var provider = services.BuildServiceProvider();

        var validatorInterfaces = ApplicationAssembly.Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .SelectMany(type => type.GetInterfaces())
            .Where(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IValidator<>))
            .Distinct()
            .ToArray();

        Assert.NotEmpty(validatorInterfaces);

        var validators = validatorInterfaces
            .Select(candidate => Assert.IsAssignableFrom<IValidator>(provider.GetService(candidate)))
            .ToArray();

        // A validator reached only through SetValidator never names a root. LocationInputValidator's
        // rules are Latitude and Longitude, but every rule that runs them carries
        // OverridePropertyName, so a screen sees `Pickup.Latitude` and keys its label on `Pickup`.
        // Counting its rules as roots would put two labels in the catalogue that no ScreenFailure
        // can ever carry - and leave the reverse test above calling them reachable.
        var children = validators
            .SelectMany(validator => validator.CreateDescriptor().Rules)
            .SelectMany(rule => rule.Components)
            .Select(component => (component.Validator as IChildValidatorAdaptor)?.ValidatorType)
            .OfType<Type>()
            .ToHashSet();

        var named = new HashSet<string>(NamedOutsideAValidator, StringComparer.Ordinal);

        foreach (var validator in validators.Where(validator => !children.Contains(validator.GetType())))
        {
            // PropertyName is already OverridePropertyName-resolved, so this is the string the
            // rule would actually write into a FieldError.
            foreach (var rule in validator.CreateDescriptor().Rules)
            {
                if (Root(rule.PropertyName) is { } root)
                {
                    named.Add(root);
                }
            }
        }

        return named;
    }

    /// <summary>
    /// The root segment a screen would key its label lookup on, taken from the production
    /// normalizer rather than from a copy of it: a second implementation here could agree with the
    /// catalogue while disagreeing with the banner.
    /// </summary>
    private static string? Root(string? propertyName) =>
        FailureKeys.For(
                new ValidationException(
                    ErrorCode.COMMON_VALIDATION_FAILED,
                    "field name probe",
                    [new FieldError(propertyName ?? string.Empty, nameof(ErrorCode.COMMON_VALIDATION_FAILED))]))
            [0]
            .Field;

    /// <summary>The entries of <c>DriveTrack.Web/Resources/FieldNames.resx</c>.</summary>
    private static Dictionary<string, string> Labels() => ValuesOf("FieldNames.resx");

    /// <summary>The entries of <c>DriveTrack.Web/Resources/UiText.resx</c>.</summary>
    private static Dictionary<string, string> UiTextValues() => ValuesOf("UiText.resx");

    private static Dictionary<string, string> ValuesOf(string fileName)
    {
        var path = Path.Combine(
            RepositoryLayout.ProjectDirectory("DriveTrack.Web"),
            "Resources",
            fileName);

        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")?.Value ?? string.Empty,
                element => element.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }
}
