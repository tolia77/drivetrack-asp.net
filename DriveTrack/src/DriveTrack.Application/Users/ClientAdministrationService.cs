using System.Globalization;
using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Authorization;
using DriveTrack.Application.Common;
using DriveTrack.Domain.Clients;
using DriveTrack.Domain.Identity;
using FluentValidation;

namespace DriveTrack.Application.Users;

/// <summary>
/// AD-3's pipeline over the client roster: open the scope → load → guard → validate → act → commit
/// → map.
/// </summary>
public sealed class ClientAdministrationService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IAccessGuard accessGuard,
    IValidator<ClientAccountState> stateValidator) : IClientAdministrationService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ClientAccount>> ListAsync(CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        // Nothing to load before the decision here: the operation is about the whole roster rather
        // than about one row, so there is no row a future rule could be about.
        accessGuard.RequireRole(UserRole.Dispatcher);

        var accounts = await unitOfWork.Users.ListByRoleAsync(UserRole.Client, cancellationToken);
        var clients = await unitOfWork.Clients.ListAsync(cancellationToken);

        // Two materialized reads joined here rather than one repository method returning a shape
        // of its own: the phone number lives on the client row and the name on the user row, and
        // AD-6 keeps each repository answering about its own table.
        var byUser = clients.ToDictionary(client => client.UserId);

        return
        [
            .. accounts
                // A client account whose row is missing is a broken invariant rather than a row to
                // render half of. It is skipped here and reported, loudly, by GetAsync.
                .Where(account => byUser.ContainsKey(account.Id))
                .Select(account => Map(account, byUser[account.Id])),
        ];
    }

    /// <inheritdoc />
    public async Task<ClientAccount> GetAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var account = await unitOfWork.Users.GetByIdAsync(userId, cancellationToken);

        // AD-3: the row is read before the decision, and nothing about it is disclosed unless the
        // guard passes.
        accessGuard.RequireRole(UserRole.Dispatcher);

        var client = await LoadClientAsync(unitOfWork, account, userId, cancellationToken);

        return Map(account!, client);
    }

    /// <inheritdoc />
    public async Task<ClientAccount> UpdateAsync(
        UserId userId,
        UpdateClientCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var account = await unitOfWork.Users.GetByIdAsync(userId, cancellationToken);

        accessGuard.RequireRole(UserRole.Admin);

        var client = await LoadClientAsync(unitOfWork, account, userId, cancellationToken);

        // AD-23. The payload is merged onto what is stored, and the *merged* record is what the
        // validator judges - so `{"firstName": "Олена"}` is not a phone number the caller failed to
        // send, it is a phone number they left alone.
        //
        // Trimmed as it is merged, not afterwards: the validator has to see the value that will
        // actually be stored, or a name of exactly the maximum length with a trailing space is
        // refused for being one character too long when the string that would reach the column
        // fits. The password is not trimmed - leading and trailing space is part of a password.
        var merged = new ClientAccountState(
            Trim(command.FirstName.Or(account!.FirstName)),
            Trim(command.LastName.Or(account.LastName)),
            Trim(command.PhoneNumber.Or(client.PhoneNumber)),

            // No current plaintext exists to merge against: absent stays null and means "unchanged".
            command.Password.HasValue ? command.Password.Value : null);

        await ValidatorExtensions.ValidateAndThrowAsync(stateValidator, merged, cancellationToken);

        var firstName = merged.FirstName!;
        var lastName = merged.LastName!;

        await unitOfWork.Users.UpdateNameAsync(userId, firstName, lastName, cancellationToken);

        client.PhoneNumber = merged.PhoneNumber!;

        if (merged.Password is not null)
        {
            await unitOfWork.Users.SetPasswordAsync(userId, merged.Password, cancellationToken);
        }

        // AD-5: the renamed user, the edited client row and the new password hash reach the
        // database here or nowhere. A password Identity refuses leaves the name unwritten too.
        await unitOfWork.CommitAsync(cancellationToken);

        return new ClientAccount(
            account.Id,
            client.Id,
            firstName,
            lastName,
            account.Email,
            client.PhoneNumber);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.CreateAsync(cancellationToken);

        var account = await unitOfWork.Users.GetByIdAsync(userId, cancellationToken);

        accessGuard.RequireRole(UserRole.Admin);

        RequireClient(account, userId);

        // The user, not the client row. Removing only the client row would leave a sign-in-able
        // account holding role Client with no subtype row - a state the mapper reports as a client
        // with a null ClientId. fk_clients_asp_net_users_user_id ON DELETE CASCADE takes the client
        // row, and PostgreSQL applies the client's own cascades transitively from there.
        await unitOfWork.Users.DeleteAsync(userId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Trims a merged field, leaving null alone so present-null still reaches the validator as the
    /// empty answer it is rather than as an empty string.
    /// </summary>
    private static string? Trim(string? value) => value?.Trim();

    private static ClientAccount Map(UserAccount account, Client client) =>
        new(
            account.Id,
            client.Id,
            account.FirstName,
            account.LastName,
            account.Email,
            client.PhoneNumber);

    /// <summary>
    /// Refuses anything that is not a client as absent, so this surface can never edit or delete an
    /// admin, a dispatcher or a driver by guessing an id.
    /// </summary>
    private static void RequireClient(UserAccount? account, UserId userId)
    {
        if (account is null || account.Role != UserRole.Client)
        {
            throw new NotFoundException(
                ErrorCode.COMMON_NOT_FOUND,
                "No client exists with user id "
                    + userId.Value.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }

    private static async Task<Client> LoadClientAsync(
        IUnitOfWork unitOfWork,
        UserAccount? account,
        UserId userId,
        CancellationToken cancellationToken)
    {
        RequireClient(account, userId);

        var client = account!.ClientId is { } clientId
            ? await unitOfWork.Clients.GetByIdAsync(clientId, cancellationToken)
            : null;

        if (client is null)
        {
            // DR-3 makes the client row part of what a client account *is*, so its absence is a
            // broken invariant rather than a caller error. It leaves as the 500 envelope rather
            // than being smoothed over into a plausible-looking row.
            throw new InvalidOperationException(
                "User " + userId.Value.ToString(CultureInfo.InvariantCulture)
                    + " holds role Client with no client row. DR-3 requires exactly one.");
        }

        return client;
    }
}
