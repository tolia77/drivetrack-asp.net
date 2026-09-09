using System.Text.Json;
using System.Text.Json.Serialization;
using DriveTrack.Application.Common;

namespace DriveTrack.Web.Api;

/// <summary>
/// AD-23's <see cref="Optional{T}"/> on the wire.
/// <para>
/// The whole mechanism rests on one property of System.Text.Json: a converter is invoked only for a
/// property that is <em>present</em> in the payload. A field the caller omitted therefore keeps
/// <c>default(Optional&lt;T&gt;)</c>, which is <see cref="Optional{T}.Absent"/>, and a field the
/// caller sent as <c>null</c> reaches <see cref="OptionalJsonConverter{T}.Read"/> and becomes
/// present-and-null. That is exactly the distinction FR-38 needs and the original system could not
/// make.
/// </para>
/// <para>
/// A factory rather than a converter, because <c>Optional&lt;T&gt;</c> is open: one registration
/// covers <c>Optional&lt;string&gt;</c>, <c>Optional&lt;int?&gt;</c> and every later field's type.
/// </para>
/// </summary>
internal sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        return typeToConvert.IsGenericType
            && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);
    }

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        var valueType = typeToConvert.GetGenericArguments()[0];

        return (JsonConverter)Activator.CreateInstance(
            typeof(OptionalJsonConverter<>).MakeGenericType(valueType))!;
    }
}

/// <summary>
/// One field's converter. It never has to write "absent": absence is the shape of a property that
/// is not there, and a command is read from the wire rather than written to it.
/// </summary>
/// <typeparam name="T">The field's type.</typeparam>
internal sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
{
    /// <summary>
    /// Whether <typeparamref name="T"/> can hold a null at all, decided once rather than per
    /// request. <c>Optional&lt;int?&gt;</c> and <c>Optional&lt;string&gt;</c> can;
    /// <c>Optional&lt;int&gt;</c>, <c>Optional&lt;decimal&gt;</c> and
    /// <c>Optional&lt;DateOnly&gt;</c> cannot.
    /// </summary>
    private static readonly bool CanHoldNull =
        Nullable.GetUnderlyingType(typeof(T)) is not null || !typeof(T).IsValueType;

    /// <inheritdoc />
    public override Optional<T> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        // Reached for an explicit null because Optional<T> is a struct: System.Text.Json hands a
        // null token to a converter whose type cannot itself be null, rather than short-circuiting.
        if (reader.TokenType == JsonTokenType.Null)
        {
            if (CanHoldNull)
            {
                // The arm FR-38 travels: `"vehicleId": null` means release the vehicle, and
                // `"nextMaintenanceDate": null` means no servicing is scheduled.
                return Optional<T>.Of(default);
            }

            // A null for a field that cannot hold one is refused rather than folded into
            // default(T). Silently reading `"mileage": null` as zero would reset an odometer on a
            // malformed payload - which is exactly the absent-versus-null ambiguity Optional<T>
            // exists to remove, reappearing one layer down.
            //
            // The contract's own failure rather than a JsonException, because a JsonException is
            // caught by the input formatter and becomes a model-state error the pipeline
            // deliberately suppresses (AD-9); a DriveTrackException propagates out of model binding
            // to ApiEnvelopeMiddleware, the single catch site, and answers 422 with the envelope.
            // No field list, deliberately. NFR-4 promises `error.fields` keys the caller can attach
            // a message to the input that produced it, and this converter cannot supply one: it is
            // handed a value and never the property it came from, and the adapter's field-name
            // mapping runs on names Application knows, which model binding has not produced yet at
            // this point. A key invented from the CLR type would name something the caller's
            // payload does not contain - a client keying off it would learn something false, which
            // is worse than a body carrying no field list at all. The envelope already models that
            // case: ValidationException documents an empty list as "the failure names no particular
            // field", and ApiError.Fields is omitted from the JSON when it is null.
            //
            // The type still reaches the log, through the message, which is where that detail
            // belongs (NFR-3).
            throw new ValidationException(
                ErrorCode.COMMON_VALIDATION_FAILED,
                "A null was sent for a field of non-nullable type " + typeof(T).Name + ".",
                []);
        }

        return Optional<T>.Of(JsonSerializer.Deserialize<T>(ref reader, options));
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (!value.HasValue)
        {
            // Nothing serializes an Optional today. Writing null rather than throwing keeps a
            // future diagnostic dump readable, and the round trip is still honest: a null read back
            // is present-and-null, which is what an absent field would mean to apply anyway.
            writer.WriteNullValue();

            return;
        }

        JsonSerializer.Serialize(writer, value.Value, options);
    }
}
