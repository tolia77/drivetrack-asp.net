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

    /// <inheritdoc />
    public override Optional<T> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return Optional<T>.Present(default!);
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
