using System.Text.Json;
using System.Text.Json.Serialization;
using DriveTrack.Domain.Identity;

namespace DriveTrack.Web.Api;

/// <summary>
/// AD-22's typed identities on the wire.
/// <para>
/// <c>UserId</c>, <c>DriverId</c> and <c>ClientId</c> are record structs, and a record struct
/// serializes as its properties — <c>{"value":3}</c>. The wrapper exists to stop two identities
/// being confused inside the system; there is nothing for a client to do with it, and a nested
/// object here would be a second wire shape for something that is a number everywhere else
/// (NFR-1). These converters write the number and read it back.
/// </para>
/// <para>
/// Registered on the one <c>JsonSerializerOptions</c> the whole adapter shares, so the envelope
/// written by middleware and the payload written through MVC agree, exactly as they do for the
/// enum-as-name converter (AD-21).
/// </para>
/// </summary>
internal sealed class UserIdJsonConverter : JsonConverter<UserId>
{
    /// <inheritdoc />
    public override UserId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetInt32());

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, UserId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteNumberValue(value.Value);
    }
}

/// <inheritdoc cref="UserIdJsonConverter" />
internal sealed class DriverIdJsonConverter : JsonConverter<DriverId>
{
    /// <inheritdoc />
    public override DriverId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetInt32());

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DriverId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteNumberValue(value.Value);
    }
}

/// <inheritdoc cref="UserIdJsonConverter" />
internal sealed class ClientIdJsonConverter : JsonConverter<ClientId>
{
    /// <inheritdoc />
    public override ClientId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetInt32());

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ClientId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteNumberValue(value.Value);
    }
}
