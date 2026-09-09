using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Identity;
using FluentValidation;

namespace DriveTrack.Application.Users;

/// <summary>
/// AD-3's pipeline over dispatcher accounts: open the scope → load → guard → validate → act →
/// commit → map.
/// </summary>
public sealed class DispatcherAdministrationService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IAccessGuard accessGuard,
    IValidator<CreateDispatcherCommand> createValidator,
    IValidator<DispatcherAccountState> stateValidator) : IDispatcherAdministrationService
{
    /// <inheritdoc />
    public async Task<DispatcherAccount> CreateAsync(
        CreateDispatcherCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        // Nothing to load: the account does not exist yet, so the guard is the first step.
        accessGuard.RequireRole(UserRole.Admin);

        // Trimmed before it is validated, not after: the validator has to judge the value that will
        // actually be stored, or a name of exactly the maximum length with a trailing space is
        // refused for being one character too long when the string that would reach the column fits.
        // The password is deliberately untrimmed - leading and trailing space is part of a password.
        var trimmed = command with
        {
            FirstName = command.FirstName?.Trim(),
            LastName = command.LastName?.Trim(),
            Email = command.Email?.Trim(),
        };

        await ValidatorExtensions.ValidateAndThrowAsync(createValidator, trimmed, cancellationToken);

        var email = trimmed.Email!;

        // The friendly answer; the normalized-email unique index behind it is the race backstop and
        // is also a 409, so two admins creating the same address at once get the same status (NFR-2).
        if (await unitOfWork.Users.EmailExistsAsync(email, cancellationToken))
        {
            throw new ConflictException(
                ErrorCode.AUTH_EMAIL_ALREADY_IN_USE,
                "An account already exists for the submitted email address.");
        }

        var account = await unitOfWork.Users.CreateAsync(
            new NewUserAccount(trimmed.FirstName!, trimmed.LastName!, email),
            trimmed.Password!,
            UserRole.Dispatcher,
            cancellationToken);

        // DR-3: no subtype row. A dispatcher is a user with a role and nothing else, which is why
        // this story adds no table and no migration.
        await unitOfWork.CommitAsync(cancellationToken);

        return Map(account);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DispatcherAccount>> ListAsync(CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        accessGuard.RequireRole(UserRole.Admin);

        var accounts = await unitOfWork.Users.ListByRoleAsync(UserRole.Dispatcher, cancellationToken);

        return [.. accounts.Select(Map)];
    }

    /// <inheritdoc />
    public async Task<DispatcherAccount> GetAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var account = await unitOfWork.Users.GetByIdAsync(userId, cancellationToken);

        accessGuard.RequireRole(UserRole.Admin);

        RequireDispatcher(account, userId);

        return Map(account!);
    }

    /// <inheritdoc />
    public async Task<DispatcherAccount> UpdateAsync(
        UserId userId,
        UpdateDispatcherCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var account = await unitOfWork.Users.GetByIdAsync(userId, cancellationToken);

        accessGuard.RequireRole(UserRole.Admin);

        RequireDispatcher(account, userId);

        // AD-23: absent means unchanged. A blank password box on the edit form is the absent case,
        // not an empty string anybody has to remember to filter out.
        //
        // Trimmed as it is merged, so the validator judges the value that will actually be stored.
        var merged = new DispatcherAccountState(
            command.FirstName.Or(account!.FirstName)?.Trim(),
            command.LastName.Or(account.LastName)?.Trim(),
            command.Password.HasValue ? command.Password.Value : null);

        await ValidatorExtensions.ValidateAndThrowAsync(stateValidator, merged, cancellationToken);

        var firstName = merged.FirstName!;
        var lastName = merged.LastName!;

        await unitOfWork.Users.UpdateNameAsync(userId, firstName, lastName, cancellationToken);

        if (merged.Password is not null)
        {
            await unitOfWork.Users.SetPasswordAsync(userId, merged.Password, cancellationToken);
        }

        await unitOfWork.CommitAsync(cancellationToken);

        return new DispatcherAccount(account.Id, firstName, lastName, account.Email);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var account = await unitOfWork.Users.GetByIdAsync(userId, cancellationToken);

        accessGuard.RequireRole(UserRole.Admin);

        RequireDispatcher(account, userId);

        await unitOfWork.Users.DeleteAsync(userId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);
    }

    private static DispatcherAccount Map(UserAccount account) =>
        new(account.Id, account.FirstName, account.LastName, account.Email);

    /// <summary>
    /// Refuses anything that is not a dispatcher as absent. An admin's id and a driver's id answer
    /// exactly what a nonexistent id answers, so this surface neither edits the wrong account nor
    /// discloses which ids are taken.
    /// </summary>
    private static void RequireDispatcher(UserAccount? account, UserId userId)
    {
        if (account is null || account.Role != UserRole.Dispatcher)
        {
            throw new NotFoundException(
                ErrorCode.COMMON_NOT_FOUND,
                "No dispatcher exists with user id "
                    + userId.Value.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }
}
