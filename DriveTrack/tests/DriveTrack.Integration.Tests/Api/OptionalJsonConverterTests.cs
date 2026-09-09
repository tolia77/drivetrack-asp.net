using System.Text.Json;
using DriveTrack.Application.Common;
using DriveTrack.Web.Api;

namespace DriveTrack.Integration.Tests.Api;

/// <summary>
/// AD-23's three wire cases, asserted on the converter itself rather than through a request.
/// <para>
/// The HTTP suites prove the distinction end to end, but they need Postgres and a whole pipeline to
/// do it, and they only ever send <c>Optional&lt;string?&gt;</c>. The two arms that carry the rule -
/// <c>HandleNull</c>, without which an explicit null short-circuits into absent, and the absent
/// write - are cheap to state directly, and stating them here is what stops the next story that
/// touches this factory from finding out over a container.
/// </para>
/// </summary>
public class OptionalJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = Build();

    [Fact]
    public void A_property_the_payload_omits_is_absent()
    {
        // Not the converter's doing: System.Text.Json passes default for a parameter nothing
        // mentioned, and default(Optional<T>) is absent. Asserted all the same, because that is the
        // reason Absent is the default value rather than a flag somebody has to set.
        var body = JsonSerializer.Deserialize<Payload>("""{"other":"x"}""", Options)!;

        Assert.False(body.Field.HasValue);
        Assert.Equal(Optional<string?>.Absent, body.Field);
    }

    [Fact]
    public void A_property_present_and_null_is_present()
    {
        var body = JsonSerializer.Deserialize<Payload>("""{"field":null}""", Options)!;

        Assert.True(body.Field.HasValue);
        Assert.Null(body.Field.Value);
    }

    [Fact]
    public void A_property_present_with_a_value_carries_it_untouched()
    {
        var body = JsonSerializer.Deserialize<Payload>("""{"field":"Passw0rd!"}""", Options)!;

        Assert.True(body.Field.HasValue);
        Assert.Equal("Passw0rd!", body.Field.Value);
    }

    [Fact]
    public void The_two_cases_the_type_exists_to_separate_do_not_deserialize_alike()
    {
        // The whole point, in one assertion: remove HandleNull and both of these read as absent.
        var omitted = JsonSerializer.Deserialize<Payload>("""{}""", Options)!;
        var explicitly = JsonSerializer.Deserialize<Payload>("""{"field":null}""", Options)!;

        Assert.NotEqual(omitted.Field, explicitly.Field);
    }

    [Fact]
    public void Writing_an_absent_value_emits_null_rather_than_throwing()
    {
        // No response in this contract carries an Optional, so this arm exists to be harmless
        // rather than to be depended on - and "harmless" means it does not touch Value, which
        // throws on an absent one by design.
        var json = JsonSerializer.Serialize(new Payload(Optional<string?>.Absent, "x"), Options);

        Assert.Contains("\"field\":null", json, StringComparison.Ordinal);
    }

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        options.Converters.Add(new OptionalJsonConverterFactory());

        return options;
    }

    private sealed record Payload(Optional<string?> Field, string Other);
}
