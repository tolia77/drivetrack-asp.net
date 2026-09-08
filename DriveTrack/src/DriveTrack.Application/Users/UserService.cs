using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Clients;
using DriveTrack.Domain.Identity;
using FluentValidation;

namespace DriveTrack.Application.Users;

/// <summary>
/// AD-3's pipeline, three times over: authenticate (the adapter has already done that and handed
/// this layer an <see cref="ICurrentUser"/>) → load → guard → validate → act → commit → map.
/// </summary>
public sealed class UserService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IAccessGuard accessGuard,
    IAccessTokenIssuer accessTokenIssuer,
    IValidator<RegisterClientCommand> registerValidator,
    IValidator<SignInCommand> signInValidator) : IUserService
{
    /// <inheritdoc />
    /// <remarks>
    /// Allowlisted in <see cref="PublicEntryPoints"/>: the caller has no account yet, so there is
    /// nothing for a guard to judge.
    /// </remarks>
    public async Task<AuthenticatedSession> RegisterClientAsync(
        RegisterClientCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await ValidatorExtensions.ValidateAndThrowAsync(registerValidator, command, cancellationToken);

        // The validator has proved these are present; the nullable annotations exist because the
        // wire can send anything and the command has to be able to carry it as far as here.
        var email = command.Email!.Trim();

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        // The friendly answer. The normalized-email unique index behind it is the race backstop and
        // surfaces as PERSISTENCE_UNIQUE_VIOLATION, which is also a 409 - so two callers registering
        // the same address at the same instant get the same status either way (NFR-2).
        if (await unitOfWork.Users.EmailExistsAsync(email, cancellationToken))
        {
            throw new ConflictException(
                ErrorCode.AUTH_EMAIL_ALREADY_IN_USE,
                "An account already exists for the submitted email address.");
        }

        var account = await unitOfWork.Users.CreateAsync(
            new NewUserAccount(command.FirstName!.Trim(), command.LastName!.Trim(), email),
            command.Password!,
            UserRole.Client,
            cancellationToken);

        var client = new Client
        {
            UserId = account.Id,
            PhoneNumber = command.PhoneNumber!.Trim(),
        };

        unitOfWork.Clients.Add(client);

        // AD-5: the user row, its role row and the client row reach the database here or nowhere.
        await unitOfWork.CommitAsync(cancellationToken);

        // The client row's own key is assigned by the commit above, so the token can carry it.
        var registered = account with { ClientId = client.Id };

        return Session(registered);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Allowlisted in <see cref="PublicEntryPoints"/>: this is how credentials are presented in the
    /// first place, so there is no caller for a guard to read.
    /// </remarks>
    public async Task<AuthenticatedSession> SignInAsync(
        SignInCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await ValidatorExtensions.ValidateAndThrowAsync(signInValidator, command, cancellationToken);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var account = await unitOfWork.Users.FindByEmailAsync(command.Email!.Trim(), cancellationToken);

        // One answer for an unknown address and for a wrong password. Distinguishing them would let
        // an attacker enumerate accounts, so both raise the same code and the same 401 body.
        if (account is null)
        {
            throw InvalidCredentials();
        }

        if (!await unitOfWork.Users.VerifyPasswordAsync(account.Id, command.Password!, cancellationToken))
        {
            throw InvalidCredentials();
        }

        // Nothing was written, so the scope is disposed without a commit and the empty transaction
        // rolls back. That is the ordinary read path, not an omission.
        return Session(account);
    }

    /// <inheritdoc />
    public async Task<UserProfile> GetProfileAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        // AD-3: load, then guard. The row is read before the decision so a future rule can be about
        // the row; nothing about it is disclosed unless the guard passes.
        var account = await unitOfWork.Users.GetByIdAsync(userId, cancellationToken);

        accessGuard.RequireSelf(userId);

        if (account is null)
        {
            throw new NotFoundException(
                ErrorCode.COMMON_NOT_FOUND,
                "No user exists with id "
                    + userId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        // FR-5: the licence number is the one field a subtype row contributes, and only for drivers.
        string? licenseNumber = null;

        if (account.Role == UserRole.Driver && account.DriverId is { } driverId)
        {
            var driver = await unitOfWork.Drivers.GetByIdAsync(driverId, cancellationToken);
            licenseNumber = driver?.LicenseNumber;
        }

        return new UserProfile(
            account.Id,
            account.FirstName,
            account.LastName,
            account.Email,
            account.Role,
            licenseNumber);
    }

    private static ForbiddenException InvalidCredentials() =>
        new(ErrorCode.AUTH_INVALID_CREDENTIALS, "Sign-in was refused: unknown email or wrong password.");

    private AuthenticatedSession Session(UserAccount account) =>
        new(
            account.Id,
            account.FirstName,
            account.LastName,
            account.Email,
            account.Role,
            accessTokenIssuer.Issue(account),
            account.DriverId,
            account.ClientId);
}
