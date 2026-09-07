using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Deliveries;
using DriveTrack.Domain.Identity;
using DriveTrack.Domain.Reviews;
using DriveTrack.Web.Api;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// System.ComponentModel.DataAnnotations declares a ValidationException too, and the probe model
// needs [Required] from that namespace. The alias states which one the throws below mean.
using ValidationException = DriveTrack.Application.Common.ValidationException;

namespace DriveTrack.Integration.Tests.Support;

/// <summary>
/// The endpoints, principal and validator the wire-contract tests call.
/// <para>
/// All of it lives in the test assembly on purpose. <c>DriveTrack.Web</c> ships no controller in
/// Epic 1, and a diagnostics controller kept only so a test has something to call is exactly the
/// empty placeholder NFR-6 forbids. <c>WebApplicationFactory</c> adds this assembly as an MVC
/// application part, so these actions run through the real <c>Program.cs</c> pipeline - the real
/// filter, the real middleware, the real suppressions - without shipping a byte of it.
/// </para>
/// </summary>
[ApiController]
[Route("api/probe")]
public sealed class ProbeController : ControllerBase
{
    /// <summary>An action returning an object: the success envelope.</summary>
    [HttpGet("success")]
    public ProbePayload Success() => new("ok", 42);

    /// <summary>An action returning an enum-bearing payload: AD-21's member name, not an ordinal.</summary>
    [HttpGet("enum")]
    public ProbeStatusPayload Enumeration() => new(DeliveryStatus.InTransit);

    /// <summary>A value-less MVC client-error result: proves <c>SuppressMapClientErrors</c>.</summary>
    [HttpGet("mvc-not-found")]
    public IActionResult MvcNotFound() => NotFound();

    /// <summary>A value-less 409, so the filter's client-error arm is pinned at a second status.</summary>
    [HttpGet("mvc-conflict")]
    public IActionResult MvcConflict() => Conflict();

    /// <summary>AD-8's typed 404.</summary>
    [HttpGet("typed-not-found")]
    public ProbePayload TypedNotFound() =>
        throw new NotFoundException(ErrorCode.COMMON_NOT_FOUND, "Probe delivery 7 does not exist.");

    /// <summary>AD-8's conflict.</summary>
    [HttpGet("conflict")]
    public ProbePayload ConflictFailure() =>
        throw new ConflictException(ErrorCode.COMMON_CONFLICT, "Probe row already exists.");

    /// <summary>AD-10's domain-rule failure: a different exception, the same status (NFR-2).</summary>
    [HttpGet("domain-rule")]
    public ProbePayload DomainRule() =>
        throw new DomainRuleException(
            ErrorCode.COMMON_CONFLICT,
            "Probe transition Delivered -> Pending is not permitted.");

    /// <summary>A validation failure naming two fields, both keys present in the catalogue.</summary>
    [HttpGet("validation")]
    public ProbePayload Validation() =>
        throw new ValidationException(
            ErrorCode.COMMON_VALIDATION_FAILED,
            "Probe validation failed.",
            [
                new FieldError("rating", nameof(ErrorCode.PERSISTENCE_CHECK_VIOLATION)),
                new FieldError("packageWeightKg", nameof(ErrorCode.COMMON_VALIDATION_FAILED)),
            ]);

    /// <summary>A validation failure whose field message key is absent from the catalogue.</summary>
    [HttpGet("validation-unknown-key")]
    public ProbePayload ValidationUnknownKey() =>
        throw new ValidationException(
            ErrorCode.COMMON_VALIDATION_FAILED,
            "Probe validation failed on an unmapped key.",
            [new FieldError("rating", UnknownMessageKey)]);

    /// <summary>
    /// A real validator run through the real seam, so the wire shape of <c>error.fields</c> is
    /// asserted against what FluentValidation actually produces rather than against a hand-built
    /// <see cref="FieldError"/> list.
    /// </summary>
    [HttpGet("validator")]
    public async Task<ProbePayload> ValidatorFailure(CancellationToken cancellationToken)
    {
        await ValidatorExtensions.ValidateAndThrowAsync(
            new ProbeCommandValidator(),
            new ProbeCommand(Title: null, Rating: 9),
            cancellationToken);

        return new ProbePayload("unreachable", 0);
    }

    /// <summary>A 204: a status HTTP forbids a body on, so the envelope must not be added.</summary>
    [HttpGet("no-content")]
    public IActionResult NoContentResponse() => NoContent();

    /// <summary>A <c>JsonResult</c>, which is not an <c>ObjectResult</c> and would otherwise escape.</summary>
    [HttpGet("json-result")]
    public IActionResult JsonResultResponse() => new JsonResult(new ProbePayload("json", 7));

    /// <summary>AD-2's guard rejection.</summary>
    [HttpGet("forbidden")]
    public ProbePayload Forbidden() =>
        throw new ForbiddenException(ErrorCode.AUTH_FORBIDDEN, "Probe principal owns no such row.");

    /// <summary>Anything unmodelled: 500, and nothing of the exception on the wire (NFR-3).</summary>
    [HttpGet("boom")]
    public ProbePayload Boom() => throw new InvalidOperationException("boom");

    /// <summary>Reached only with credentials; without them the challenge writer answers.</summary>
    [Authorize]
    [HttpGet("authenticated")]
    public ProbePayload Authenticated() => new("authenticated", 1);

    /// <summary>Reached only by a principal holding the probe claim; otherwise the forbid writer answers.</summary>
    [Authorize(Policy = ProbeAuthentication.PolicyName)]
    [HttpGet("authorized")]
    public ProbePayload Authorized() => new("authorized", 1);

    /// <summary>
    /// A model with a required property missing. With <c>SuppressModelStateInvalidFilter</c> on, the
    /// body executes and reports what the model state said instead of the framework answering 400.
    /// </summary>
    [HttpPost("model-state")]
    public ProbeModelStatePayload PostModel([FromBody] ProbeModel model) =>
        new(ModelState.IsValid, model.Name);

    /// <summary>Echoes the request culture, so NFR-14's fixed locale can be asserted over HTTP.</summary>
    [HttpGet("culture")]
    public ProbeCulturePayload Culture() =>
        new(CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name);

    /// <summary>
    /// The one probe that reaches the database, so the story's headline defect can be asserted end to
    /// end. The translation at <c>CommitAsync</c> and the envelope at the adapter are otherwise
    /// proven in two suites that never meet, and "a unique violation is a 409 on the wire" is a
    /// claim about the join rather than about either half.
    /// </summary>
    [HttpPost("persist-review")]
    public async Task<ProbePayload> PersistReview(
        [FromServices] IUnitOfWorkFactory unitOfWorkFactory,
        [FromQuery] int deliveryId,
        [FromQuery] int clientId,
        [FromQuery] int rating,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWorkFactory);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        unitOfWork.Reviews.Add(new Review
        {
            DeliveryId = deliveryId,
            ClientId = new ClientId(clientId),
            Rating = rating,
            Text = "Wire-contract probe",
            CreatedAt = Seed.Instant,
        });

        await unitOfWork.CommitAsync(cancellationToken);

        return new ProbePayload("persisted", rating);
    }

    /// <summary>A key deliberately absent from <c>ErrorMessages.resx</c>, so the fallback is exercised.</summary>
    public const string UnknownMessageKey = "PROBE_KEY_THAT_IS_NOT_IN_THE_CATALOGUE";
}

/// <summary>A plain payload for the success rows.</summary>
public sealed record ProbePayload(string Name, int Value);

/// <summary>A payload carrying a domain enum (AD-21).</summary>
public sealed record ProbeStatusPayload(DeliveryStatus Status);

/// <summary>What the model-state action reports back.</summary>
public sealed record ProbeModelStatePayload(bool ModelStateIsValid, string? Name);

/// <summary>The culture the request ran under.</summary>
public sealed record ProbeCulturePayload(string Culture, string UiCulture);

/// <summary>A body whose required property the test deliberately omits.</summary>
public sealed class ProbeModel
{
    /// <summary>Required, so an absent value would normally trip the model-state filter.</summary>
    [Required]
    public string? Name { get; set; }
}

/// <summary>A command with one rule, for the <c>ValidateAndThrowAsync</c> rows (AD-9).</summary>
/// <param name="Title">Must be present.</param>
/// <param name="Rating">Must be 1 to 5.</param>
public sealed record ProbeCommand(string? Title, int Rating);

/// <summary>
/// The validator for <see cref="ProbeCommand"/>. Its messages are resource keys rather than
/// sentences, which is the convention the adapter's field localization depends on.
/// </summary>
public sealed class ProbeCommandValidator : AbstractValidator<ProbeCommand>
{
    /// <summary>Declares the two rules the validation rows exercise.</summary>
    public ProbeCommandValidator()
    {
        RuleFor(command => command.Title)
            .NotEmpty()
            .WithMessage(nameof(ErrorCode.COMMON_VALIDATION_FAILED));

        RuleFor(command => command.Rating)
            .InclusiveBetween(1, 5)
            .WithMessage(nameof(ErrorCode.PERSISTENCE_CHECK_VIOLATION));
    }
}

/// <summary>Names shared by the probe authentication handler, its policy and the tests.</summary>
public static class ProbeAuthentication
{
    /// <summary>The scheme the test host makes default.</summary>
    public const string SchemeName = "Probe";

    /// <summary>The policy the authorized action requires.</summary>
    public const string PolicyName = "ProbePolicy";

    /// <summary>Claim the policy demands.</summary>
    public const string ClaimType = "probe";

    /// <summary>Value the policy demands.</summary>
    public const string ClaimValue = "allowed";

    /// <summary>Header carrying the probe principal's name; absent means unauthenticated.</summary>
    public const string UserHeader = "X-Probe-User";

    /// <summary>Header that, when present, grants the principal the policy's claim.</summary>
    public const string ClaimHeader = "X-Probe-Claim";
}

/// <summary>
/// A minimal scheme whose only job is to reach AD-7's third suppression: its challenge and forbid
/// overrides call the production <see cref="EnvelopeAuthenticationEvents"/> methods, which is
/// exactly what Epic 2 will do from the cookie and JWT handlers' events. Proving them here means
/// the 401 and 403 shapes are settled before either real scheme exists.
/// </summary>
public sealed class ProbeAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ProbeAuthentication.UserHeader, out var user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, user.ToString()) };

        if (Request.Headers.ContainsKey(ProbeAuthentication.ClaimHeader))
        {
            claims.Add(new Claim(ProbeAuthentication.ClaimType, ProbeAuthentication.ClaimValue));
        }

        var identity = new ClaimsIdentity(claims, ProbeAuthentication.SchemeName);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            ProbeAuthentication.SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        EnvelopeAuthenticationEvents.WriteChallengeAsync(Context);

    /// <inheritdoc />
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        EnvelopeAuthenticationEvents.WriteForbiddenAsync(Context);
}
