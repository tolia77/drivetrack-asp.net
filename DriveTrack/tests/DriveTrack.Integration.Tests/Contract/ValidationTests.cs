using DriveTrack.Application;
using DriveTrack.Application.Common;
using DriveTrack.Integration.Tests.Support;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using ValidationException = DriveTrack.Application.Common.ValidationException;

namespace DriveTrack.Integration.Tests.Contract;

/// <summary>
/// AD-9: validation is a call an application service makes, not something an adapter pipeline does
/// on its behalf.
/// <para>
/// That is a boundary, so it gets a boundary assertion as well as a behavioural one. The seam is
/// only worth having while nothing else can reach a validator - the moment an MVC filter runs one,
/// "authorize then validate" (AD-3) stops being a property of the operation pipeline and becomes a
/// coincidence of middleware ordering.
/// </para>
/// </summary>
public class ValidationTests
{
    [Fact]
    public void The_seam_is_called_by_name_because_FluentValidation_declares_the_same_signature()
    {
        // FluentValidation's own ValidateAndThrowAsync has this exact shape and throws its own
        // ValidationException - which carries no ErrorCode and so has no status. The collision is a
        // compile error rather than a silent substitution, but it is why every call in this suite
        // names ValidatorExtensions explicitly, and why an application service must do the same.
        var ours = typeof(ValidatorExtensions).GetMethod(nameof(ValidatorExtensions.ValidateAndThrowAsync));

        Assert.NotNull(ours);
        Assert.Equal(typeof(Task), ours.ReturnType);
    }

    [Fact]
    public async Task A_failing_command_throws_naming_every_offending_field()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var validator = new ProbeCommandValidator();

        var failure = await Assert.ThrowsAsync<ValidationException>(
            () => ValidatorExtensions.ValidateAndThrowAsync(
                validator,
                new ProbeCommand(Title: null, Rating: 9),
                cancellationToken));

        Assert.Equal(ErrorCode.COMMON_VALIDATION_FAILED, failure.Code);

        // One FieldError per failure, not one per request: a form that reports its errors one at a
        // time is a form filled in as many times (NFR-4).
        Assert.Equal(
            ["Rating", "Title"],
            failure.FieldErrors.Select(field => field.Field).Order().ToArray());
    }

    [Fact]
    public async Task A_valid_command_returns_without_throwing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var validator = new ProbeCommandValidator();

        await ValidatorExtensions.ValidateAndThrowAsync(
            validator,
            new ProbeCommand("Title", Rating: 3),
            cancellationToken);
    }

    [Fact]
    public async Task A_field_message_is_a_resource_key_the_catalogue_resolves()
    {
        // The convention the adapter's field localization depends on: a validator states a key, not
        // a sentence, so nothing it writes can reach a user untranslated (NFR-14).
        var cancellationToken = TestContext.Current.CancellationToken;
        var validator = new ProbeCommandValidator();

        var failure = await Assert.ThrowsAsync<ValidationException>(
            () => ValidatorExtensions.ValidateAndThrowAsync(
                validator,
                new ProbeCommand("Title", Rating: 9),
                cancellationToken));

        var key = Assert.Single(failure.FieldErrors).MessageKey;

        Assert.Contains(key, Enum.GetNames<ErrorCode>(), StringComparer.Ordinal);
    }

    [Fact]
    public void Every_validator_in_the_application_assembly_resolves_from_AddApplication()
    {
        // Discovery is from ApplicationAssembly.Assembly, so a validator added to the layer is
        // registered by having been written - no second list to keep in step.
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

        var unresolved = validatorInterfaces
            .Where(candidate => provider.GetService(candidate) is null)
            .Select(candidate => candidate.Name)
            .ToArray();

        Assert.Empty(unresolved);
    }

    [Fact]
    public void The_discovery_seam_finds_the_validators_it_is_pointed_at()
    {
        // Epic 1 ships no command, so the assertion above is vacuously true today. This does not
        // close that hole: it calls AddValidatorsFromAssembly directly rather than AddApplication,
        // so it proves only that FluentValidation's discovery finds a validator in an assembly it is
        // pointed at. It does not prove AddApplication points at the right one - only a validator
        // actually living in DriveTrack.Application can, and Epic 2 ships the first.
        var services = new ServiceCollection();
        services.AddValidatorsFromAssembly(typeof(ProbeCommandValidator).Assembly);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IValidator<ProbeCommand>>());
    }

    [Fact]
    public void No_file_under_the_web_project_names_FluentValidation()
    {
        // AD-9's boundary. The adapter must have no way to run a validator, which starts with it
        // having no way to name one.
        var webDirectory = RepositoryLayout.ProjectDirectory("DriveTrack.Web");

        var offenders = Directory
            .EnumerateFiles(webDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsBuildOutput(path, webDirectory))
            .Where(path => File.ReadAllText(path).Contains("FluentValidation", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(webDirectory, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static bool IsBuildOutput(string path, string root)
    {
        var segments = Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }
}
