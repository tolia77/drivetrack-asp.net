namespace DriveTrack.Application.Common;

/// <summary>
/// The closed failure vocabulary (AD-8). Every member is <c>SCREAMING_SNAKE</c> and carries the
/// namespace prefix of the capability that owns it; a capability may only mint codes in its own
/// namespace, and the enum grows one capability at a time rather than being invented per endpoint.
/// <para>
/// Two rules hold every member together and are asserted by <c>ErrorContractTests</c>: each has a
/// resource key of the same name in <c>ErrorMessages.resx</c> (AD-18), and each has a status arm in
/// the adapter's status map (AD-7). Nothing is minted here that no shipped code path can produce.
/// </para>
/// </summary>
#pragma warning disable CA1707 // SCREAMING_SNAKE is the contract (AD-8); the wire sees these names.
public enum ErrorCode
{
    /// <summary>Anything that escaped unhandled. 500, and the only code whose cause is not modelled.</summary>
    COMMON_UNEXPECTED_ERROR,

    /// <summary>The addressed resource does not exist, or the caller may not learn that it does. 404.</summary>
    COMMON_NOT_FOUND,

    /// <summary>The request was understood and refused on its content. 422 (NFR-4).</summary>
    COMMON_VALIDATION_FAILED,

    /// <summary>The request collides with the current state of another row or a domain rule. 409.</summary>
    COMMON_CONFLICT,

    /// <summary>No usable credentials were presented. 401; FR-13 branches on this single code.</summary>
    AUTH_UNAUTHENTICATED,

    /// <summary>Credentials were presented and do not permit the operation. 403.</summary>
    AUTH_FORBIDDEN,

    /// <summary>
    /// The email is unknown or the password is wrong. 401, and deliberately one code for both:
    /// a distinguishable answer lets an attacker enumerate accounts (FR-4).
    /// </summary>
    AUTH_INVALID_CREDENTIALS,

    /// <summary>Registration named an email another account already holds. 409 (FR-1).</summary>
    AUTH_EMAIL_ALREADY_IN_USE,

    /// <summary>The email is absent or not a well-formed address. 422 (FR-3).</summary>
    AUTH_EMAIL_INVALID,

    /// <summary>The phone number is not in E.164 form. 422 (FR-3).</summary>
    AUTH_PHONE_NUMBER_INVALID,

    /// <summary>The password is below the configured policy. 422 (FR-3, FR-8).</summary>
    AUTH_PASSWORD_TOO_WEAK,

    /// <summary>The confirmation does not equal the password. 422 (FR-3).</summary>
    AUTH_PASSWORD_CONFIRMATION_MISMATCH,

    /// <summary>
    /// The named vehicle is already held by another driver. 409 (FR-44). Distinct from
    /// <see cref="FLEET_VEHICLE_IN_USE"/>: this refuses an assignment, that one refuses a deletion.
    /// </summary>
    FLEET_VEHICLE_ALREADY_ASSIGNED,

    /// <summary>A driver holds this vehicle, so it cannot be deleted. 409 (FR-43).</summary>
    FLEET_VEHICLE_IN_USE,

    /// <summary>Another vehicle already carries this licence plate. 409 (FR-41).</summary>
    FLEET_LICENSE_PLATE_IN_USE,

    /// <summary>
    /// A delivery named a driver no row holds. 404 (FR-29). Distinct from
    /// <see cref="COMMON_NOT_FOUND"/>: the delivery being addressed exists, and the missing row is
    /// one the payload named — a caller can act on that by choosing another driver.
    /// </summary>
    DELIVERY_DRIVER_NOT_FOUND,

    /// <summary>A delivery named a client no row holds. 404 (FR-29).</summary>
    DELIVERY_CLIENT_NOT_FOUND,

    /// <summary>
    /// The delivery would be left assigned to a driver whose vehicle cannot carry it. 409 (FR-103).
    /// Raised from either side of the invariant: assigning a driver or changing a weight here, and
    /// changing a driver's vehicle over in the fleet.
    /// </summary>
    DELIVERY_EXCEEDS_VEHICLE_CAPACITY,

    /// <summary>
    /// The delivery's current status does not lead to the one that was asked for. 409 (FR-32),
    /// never 422: the payload named a status the system has, and it is the row's state that refuses
    /// it, so the caller's next move is to look at where the delivery actually is (AD-10).
    /// </summary>
    DELIVERY_INVALID_STATUS_TRANSITION,

    /// <summary>
    /// The delivery cannot be marked delivered: it has neither a proof nor an explanatory note
    /// (FR-120). 409, never 422 — the payload was well formed and it is the state of the delivery
    /// that refuses it, so the caller's next move is to capture the proof or say why there is none.
    /// Evaluated for every caller the guard admits, with no role branch anywhere (AD-10).
    /// </summary>
    DELIVERY_PROOF_REQUIRED,

    /// <summary>
    /// The delivery already has a proof, and a proof is never replaced (FR-123). 409: a genuine
    /// collision with a row that already exists, like every other 409 here.
    /// </summary>
    DELIVERY_PROOF_ALREADY_CAPTURED,

    /// <summary>
    /// An uploaded artefact declares a type a proof may not carry (NFR-28). 422, and reported as a
    /// field key inside <c>COMMON_VALIDATION_FAILED</c> — it names the upload that was wrong.
    /// </summary>
    DELIVERY_PROOF_ASSET_TYPE_NOT_ALLOWED,

    /// <summary>
    /// An uploaded artefact is empty or above the per-asset byte cap (NFR-28). 422, reported as a
    /// field key for the same reason as the type refusal above.
    /// </summary>
    DELIVERY_PROOF_ASSET_TOO_LARGE,

    /// <summary>
    /// No driver holds the id a conversation was addressed by, so there is no conversation keyed on
    /// it. 404 (FR-71). Answered only after the guard has passed: a caller who may not reach the
    /// thread is refused before learning whether it exists.
    /// </summary>
    CHAT_THREAD_NOT_FOUND,

    /// <summary>
    /// The message body is empty once trimmed. 422 (FR-70), reported as a field key inside
    /// <c>COMMON_VALIDATION_FAILED</c> so the composer can say which box was wrong.
    /// </summary>
    CHAT_MESSAGE_TEXT_REQUIRED,

    /// <summary>
    /// The message body is longer than the <c>messages.text</c> column holds. 422 (FR-70), reported
    /// as a field key for the same reason as the refusal above — and stated by the validator so a
    /// truncation the database would raise arrives as a 422 naming the field rather than a 500.
    /// </summary>
    CHAT_MESSAGE_TEXT_TOO_LONG,

    /// <summary>PostgreSQL SQLSTATE 23505, translated in Infrastructure. 409 (AD-8).</summary>
    PERSISTENCE_UNIQUE_VIOLATION,

    /// <summary>PostgreSQL SQLSTATE 23514, translated in Infrastructure. 422 (AD-8).</summary>
    PERSISTENCE_CHECK_VIOLATION,
}
#pragma warning restore CA1707
