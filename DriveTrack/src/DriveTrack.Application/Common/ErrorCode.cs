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

    /// <summary>
    /// A field a user types into was left empty. 422, and the one code every bare
    /// <c>NotEmpty</c>/<c>NotNull</c> rule in the system shares.
    /// <para>
    /// Sharing is right here and wrong for a bounded rule. "Обов'язкове поле" is the whole of what
    /// there is to say about an empty box, and it is only readable because the field travels beside
    /// it: <c>DtFailureBanner</c> prefixes the localized label and <c>DtFieldError</c> renders the
    /// sentence under the input itself, so "which box" is answered by where the sentence is rather
    /// than by minting one code per property.
    /// </para>
    /// </summary>
    COMMON_FIELD_REQUIRED,

    /// <summary>
    /// A paging offset below zero. 422, and deliberately shared across every list query: no screen
    /// offers a box for it, so there is no input for a field-specific sentence to sit under and
    /// nothing a user could have typed differently (NFR-27).
    /// </summary>
    COMMON_PAGING_OFFSET_INVALID,

    /// <summary>A paging limit outside one-to-a-hundred. 422, shared for the same reason.</summary>
    COMMON_PAGING_LIMIT_INVALID,

    /// <summary>
    /// A coordinate outside the ranges a map has. 422, stated by <c>LocationInputValidator</c> so
    /// the refusal names the point the caller sent rather than arriving as
    /// <see cref="MapLocation"/>'s constructor throwing into the unexpected-error arm.
    /// </summary>
    COMMON_COORDINATE_OUT_OF_RANGE,

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
    /// The current password offered alongside a new one does not match the stored hash. 422
    /// (FR-88), and deliberately not <see cref="AUTH_INVALID_CREDENTIALS"/>.
    /// <para>
    /// The caller is authenticated: they are signed in and changing their own password. A 401 would
    /// be read by FR-13's boundary as "the session has gone" and would bounce the user to sign-in,
    /// and read by a human the same way — both of which are false. What was refused is one field of
    /// the request, so it is a 422 on the content like every other refusal of that shape.
    /// </para>
    /// </summary>
    AUTH_CURRENT_PASSWORD_INCORRECT,

    /// <summary>
    /// A given name longer than the <c>first_name</c> column holds. 422, reported as a field key —
    /// so a truncation PostgreSQL would raise arrives naming the box instead.
    /// </summary>
    AUTH_FIRST_NAME_TOO_LONG,

    /// <summary>A family name longer than the <c>last_name</c> column holds. 422, for the same reason.</summary>
    AUTH_LAST_NAME_TOO_LONG,

    /// <summary>
    /// A password above the ceiling every path that accepts one puts on it - registration, sign-in,
    /// taking on a driver, an administrator setting somebody's password, and a user changing their
    /// own. 422, and distinct from <see cref="AUTH_PASSWORD_TOO_WEAK"/>: nothing about it is weak,
    /// it is simply longer than the hasher will be asked to chew through, which matters most on the
    /// two endpoints an unauthenticated caller can reach.
    /// <para>
    /// The split is the point. <see cref="AUTH_PASSWORD_TOO_WEAK"/> answers with the policy - eight
    /// characters, both cases, a digit - which read against a two-hundred-character password is
    /// advice to make it longer still, the one change that cannot help.
    /// </para>
    /// </summary>
    AUTH_PASSWORD_TOO_LONG,

    /// <summary>
    /// The named vehicle is already held by another driver. 409 (FR-44). Distinct from
    /// <see cref="FLEET_VEHICLE_IN_USE"/>: this refuses an assignment, that one refuses a deletion.
    /// </summary>
    FLEET_VEHICLE_ALREADY_ASSIGNED,

    /// <summary>A driver holds this vehicle, so it cannot be deleted. 409 (FR-43).</summary>
    FLEET_VEHICLE_IN_USE,

    /// <summary>Another vehicle already carries this licence plate. 409 (FR-41).</summary>
    FLEET_LICENSE_PLATE_IN_USE,

    /// <summary>A model name longer than the <c>vehicles.model</c> column holds. 422 (FR-40).</summary>
    FLEET_MODEL_TOO_LONG,

    /// <summary>A plate longer than the <c>vehicles.license_plate</c> column holds. 422 (FR-40).</summary>
    FLEET_LICENSE_PLATE_TOO_LONG,

    /// <summary>A capacity of zero or less, which is not a vehicle that can carry anything. 422.</summary>
    FLEET_CAPACITY_NOT_POSITIVE,

    /// <summary>
    /// A capacity above the magnitude <c>numeric(10, 2)</c> holds. 422, so an over-wide figure is a
    /// refusal naming the box rather than a numeric overflow at the commit.
    /// </summary>
    FLEET_CAPACITY_TOO_LARGE,

    /// <summary>A negative odometer reading. 422 — an odometer does not run backwards.</summary>
    FLEET_MILEAGE_NEGATIVE,

    /// <summary>
    /// A licence number longer than the <c>drivers.license_number</c> column holds. 422 (FR-35).
    /// </summary>
    FLEET_LICENSE_NUMBER_TOO_LONG,

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

    /// <summary>Package details longer than the <c>package_details</c> column holds. 422 (FR-14).</summary>
    DELIVERY_PACKAGE_DETAILS_TOO_LONG,

    /// <summary>Delivery notes longer than the <c>delivery_notes</c> column holds. 422 (FR-17).</summary>
    DELIVERY_NOTES_TOO_LONG,

    /// <summary>
    /// A package weight of zero or less. 422, and the friendly half of
    /// <c>ck_deliveries_package_weight_kg</c> — the constraint is the backstop and carries no field,
    /// this is what lets the form mark the box (NFR-2, NFR-4).
    /// </summary>
    DELIVERY_PACKAGE_WEIGHT_NOT_POSITIVE,

    /// <summary>A package weight above the magnitude <c>numeric(10, 3)</c> holds. 422 (FR-102).</summary>
    DELIVERY_PACKAGE_WEIGHT_TOO_LARGE,

    /// <summary>
    /// A delivery window whose latest bound is not after its earliest one. 422 (FR-100), and the
    /// friendly half of <c>ck_deliveries_delivery_window</c>. Reported on the latest bound, because
    /// that is the field a dispatcher would move to fix it.
    /// </summary>
    DELIVERY_WINDOW_ENDS_BEFORE_IT_STARTS,

    /// <summary>
    /// A timeline note longer than the <c>timeline_entries.note</c> column holds. 422 (FR-107),
    /// shared by the standalone-note path and the note that rides on a status change, because both
    /// write the same column through the same rule on the entity that owns it.
    /// </summary>
    DELIVERY_NOTE_TOO_LONG,

    /// <summary>
    /// A status the system does not have. 422, and deliberately not
    /// <see cref="DELIVERY_INVALID_STATUS_TRANSITION"/>: that one refuses a real status the row
    /// cannot reach, this one refuses a value that names no status at all. The converter is
    /// registered with <c>allowIntegerValues: true</c>, so <c>{"status": 42}</c> deserializes to an
    /// undefined member and has to be refused here.
    /// </summary>
    DELIVERY_STATUS_UNKNOWN,

    /// <summary>
    /// A recipient name longer than the <c>proof_of_deliveries.recipient_name</c> column holds.
    /// 422 (FR-119).
    /// </summary>
    DELIVERY_RECIPIENT_NAME_TOO_LONG,

    /// <summary>
    /// A capture carrying no signature, or more than one. 422 (FR-119): the three shape refusals
    /// below are separate codes because they are three different sentences to somebody standing at
    /// a door holding a phone.
    /// </summary>
    DELIVERY_PROOF_SIGNATURE_REQUIRED,

    /// <summary>A capture carrying no photograph. 422 (FR-119).</summary>
    DELIVERY_PROOF_PHOTO_REQUIRED,

    /// <summary>A capture carrying more photographs than <c>ProofAssetRules.MaximumPhotos</c>. 422.</summary>
    DELIVERY_PROOF_TOO_MANY_PHOTOS,

    /// <summary>
    /// An address search below the floor that keeps every keystroke from becoming an outbound
    /// geocoding request. 422 (FR-104), refused before any call leaves.
    /// </summary>
    DELIVERY_PLACE_QUERY_TOO_SHORT,

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

    /// <summary>
    /// A review was attempted on a delivery that has not finished (FR-62). 422, and deliberately
    /// not 409: the caller named a delivery that exists and is theirs, and what refused them is a
    /// value in the request — the delivery they chose — rather than a collision with another row.
    /// The next move is to pick a different delivery, which is what a 422 on the content says.
    /// </summary>
    REVIEW_DELIVERY_NOT_COMPLETED,

    /// <summary>
    /// The rating is outside the one-to-five scale (FR-67). 422, reported as a field key inside
    /// <c>COMMON_VALIDATION_FAILED</c> so the form can mark the control that was wrong — and stated
    /// by the validator so the answer is the same 422 whether it or <c>CK_Reviews_Rating</c>
    /// catches it (NFR-2).
    /// </summary>
    REVIEW_RATING_OUT_OF_RANGE,

    /// <summary>The review text is empty once trimmed. 422 (FR-62), reported as a field key.</summary>
    REVIEW_TEXT_REQUIRED,

    /// <summary>
    /// The review text is longer than the <c>reviews.text</c> column holds. 422 (FR-62), reported
    /// as a field key — so a truncation PostgreSQL would raise arrives naming the field instead.
    /// </summary>
    REVIEW_TEXT_TOO_LONG,

    /// <summary>
    /// The driver already has an open shift, so a second one may not be started (FR-110). 409: the
    /// request collides with a row that already exists, and the caller's next move is to end the
    /// shift they are already on rather than to re-spell the request.
    /// <para>
    /// The friendly half of a rule the database owns. <c>IX_Shifts_DriverId_Open</c> is what
    /// actually enforces it — two requests racing both pass a pre-insert lookup — and the loser of
    /// a race gets <see cref="PERSISTENCE_UNIQUE_VIOLATION"/>, which is the same 409 (NFR-2).
    /// </para>
    /// </summary>
    SHIFT_ALREADY_OPEN,

    /// <summary>
    /// The driver has no open shift, so there is nothing to end (FR-110). 409 for the same reason
    /// the code above is one: the payload named a driver who exists and it is the state of their
    /// shifts that refuses the request.
    /// </summary>
    SHIFT_NOT_OPEN,

    /// <summary>
    /// A shift would end before it started (FR-111). 422, reported as a field key inside
    /// <c>COMMON_VALIDATION_FAILED</c> so the form can mark the box that was wrong — and judged
    /// against the state the row will hold rather than against the payload, so a one-field edit is
    /// refused on the window it actually produces (AD-23).
    /// </summary>
    SHIFT_ENDED_BEFORE_STARTED,

    /// <summary>PostgreSQL SQLSTATE 23505, translated in Infrastructure. 409 (AD-8).</summary>
    PERSISTENCE_UNIQUE_VIOLATION,

    /// <summary>PostgreSQL SQLSTATE 23514, translated in Infrastructure. 422 (AD-8).</summary>
    PERSISTENCE_CHECK_VIOLATION,
}
#pragma warning restore CA1707
