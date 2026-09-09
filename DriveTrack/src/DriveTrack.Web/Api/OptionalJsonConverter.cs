using System.Text.Json;
using System.Text.Json.Serialization;
using DriveTrack.Application.Common;

namespace DriveTrack.Web.Api;

/// <summary>
/// AD-23's <see cref="Optional{T}"/> on the wire.
/// <para>
/// Without this, a partial update cannot tell the two cases apart at all. <c>Optional&lt;T&gt;</c>
/// is a struct with public <c>HasValue</c> and <c>Value</c> members, so the default serializer would
/// read <c>{"password": "x"}</c> as an object it could not construct and would touch <c>Value</c> on
/// an absent one, which throws by design. The mapping this factory installs is the plain one a
/// caller expects: <b>a property the payload omits is absent</b>, a property present with any value
/// — <c>null</c> included — is present.
/// </para>
/// <para>
/// The omitted case never reaches a converter: <c>System.Text.Json</c> passes <c>default</c> for a
/// constructor parameter the payload did not mention, and <c>default(Optional&lt;T&gt;)</c> is
/// absent. That is why <see cref="Optional{T}.Absent"/> is the default value and not a flag someone
/// has to set.
/// </para>
/// <para>
/// Registered on the one <c>JsonSerializerOptions</c> the whole adapter shares, beside the typed-id
/// and enum converters, so every reader and writer in the process agrees (AD-21, AD-22).
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

        var wrapped = typeToConvert.GetGenericArguments()[0];

        return (JsonConverter)Activator.CreateInstance(
            typeof(OptionalJsonConverter<>).MakeGenericType(wrapped))!;
    }
}

/// <inheritdoc cref="OptionalJsonConverterFactory" />
/// <typeparam name="T">The wrapped value's type.</typeparam>
internal sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
{
    /// <summary>
    /// True, and load-bearing. A <c>null</c> in the payload is the *present*-null case — the caller
    /// sent the field — and the serializer would otherwise short-circuit it into <c>default</c>,
    /// which is absent: the two cases this type exists to separate, silently merged again.
    /// </summary>
    public override bool HandleNull => true;

    /// <summary>
    /// Whether a <c>null</c> is a value this field could actually hold.
    /// <c>Optional&lt;int?&gt;</c> and <c>Optional&lt;string&gt;</c> can;
    /// <c>Optional&lt;int&gt;</c>, <c>Optional&lt;decimal&gt;</c> and <c>Optional&lt;DateOnly&gt;</c>
    /// cannot.
    /// </summary>
    private static readonly bool CanHoldNull =
        Nullable.GetUnderlyingType(typeof(T)) is not null || !typeof(T).IsValueType;

    /// <inheritdoc />
    public override Optional<T> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            if (CanHoldNull)
            {
                // The arm FR-38 travels: `"vehicleId": null` releases the vehicle, and
                // `"nextMaintenanceDate": null` means no servicing is scheduled.
                return Optional<T>.Present(default!);
            }

            // A null for a field that cannot hold one is refused rather than folded into
            // default(T). Reading `"mileage": null` as zero would reset an odometer on a malformed
            // payload - the absent-versus-null ambiguity this type exists to remove, reappearing
            // one layer down.
            //
            // The contract's own failure rather than a JsonException: a JsonException is caught by
            // the input formatter and becomes a model-state error the pipeline deliberately
            // suppresses (AD-9), while a DriveTrackException propagates out of model binding to
            // ApiEnvelopeMiddleware and answers 422 with the envelope.
            //
            // No field list, deliberately. NFR-4 promises `error.fields` keys the caller can attach
            // a message to, and this converter is handed a value and never the property it came
            // from. A key invented from the CLR type would name an input the caller does not have.
            throw new ValidationException(
                ErrorCode.COMMON_VALIDATION_FAILED,
                "A null was sent for a field of non-nullable type " + typeof(T).Name + ".",
                []);
        }

        return Optional<T>.Present(JsonSerializer.Deserialize<T>(ref reader, options)!);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // Absent is written as null rather than by omitting the property, because a converter
        // cannot omit one. No response payload in this contract carries an Optional, so this arm is
        // the honest answer to a shape nothing sends rather than a case anybody depends on.
        if (!value.HasValue)
        {
            writer.WriteNullValue();

            return;
        }

        JsonSerializer.Serialize(writer, value.Value, options);
    }
}
