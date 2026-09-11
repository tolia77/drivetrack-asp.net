using DriveTrack.Application.Abstractions;
using DriveTrack.Application.Common;

namespace DriveTrack.Application.Users;

/// <summary>
/// FR-92's uniqueness rule, asked once.
/// <para>
/// Two callers change an address: a user changing their own (<see cref="IUserService"/>) and an
/// administrator editing a client (<see cref="IClientAdministrationService"/>). Written twice, the
/// two would be one edit apart from disagreeing about whether re-casing your own address is a
/// conflict — so the decision lives here and both call it.
/// </para>
/// </summary>
internal static class EmailChange
{
    /// <summary>
    /// Stages the account's new address, or answers the stored one when nothing is changing.
    /// </summary>
    /// <param name="unitOfWork">The caller's open scope; the write lands at its single commit.</param>
    /// <param name="account">The account as it is stored, loaded and guarded by the caller.</param>
    /// <param name="email">The address the caller asked for, already judged well-formed.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The address the account will hold once the caller commits.</returns>
    /// <exception cref="ConflictException">Another account already holds that address.</exception>
    /// <remarks>
    /// The unchanged case is answered first, and it has to be. <c>EmailExistsAsync</c> answers true
    /// for the caller's own address, so a form saved without the address box being touched would
    /// otherwise be refused as a conflict with itself.
    /// <para>
    /// The comparison is ordinal-ignore-case because Application may not reach Identity's normalizer
    /// (AD-1). Identity's default normalizer is upper-invariant, so the two agree on every case that
    /// matters here. Behind both is <c>user_name_index</c>, the unique index on
    /// <c>normalized_user_name</c> that <see cref="IUserAccountRepository.SetEmailAsync"/> writes
    /// into — <c>normalized_email</c> carries an index but not a unique one.
    /// </para>
    /// </remarks>
    public static async Task<string> ApplyAsync(
        IUnitOfWork unitOfWork,
        UserAccount account,
        string email,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(account);

        var requested = email.Trim();

        if (string.Equals(requested, account.Email, StringComparison.OrdinalIgnoreCase))
        {
            // Nothing to write, and nothing to refuse. The stored spelling is returned rather than
            // the caller's, because re-casing an address is not a change this system makes.
            return account.Email;
        }

        // The friendly answer. The backstop behind it is `user_name_index`, the unique index on
        // normalized_user_name - not `email_index`, which the schema creates without `unique`. That
        // is only a backstop at all because SetEmailAsync writes the user-name pair alongside the
        // email pair; trim that method to the email columns and two callers claiming one address at
        // the same instant would both be written. It surfaces as PERSISTENCE_UNIQUE_VIOLATION,
        // which is also a 409, so the race and the friendly answer agree on the status (NFR-2).
        if (await unitOfWork.Users.EmailExistsAsync(requested, cancellationToken))
        {
            throw new ConflictException(
                ErrorCode.AUTH_EMAIL_ALREADY_IN_USE,
                "Another account already holds the submitted email address.");
        }

        await unitOfWork.Users.SetEmailAsync(account.Id, requested, cancellationToken);

        return requested;
    }
}
