using System.Reflection;
using System.Runtime.CompilerServices;
using DriveTrack.Domain.Identity;

namespace DriveTrack.Integration.Tests.Domain;

/// <summary>
/// AD-22 at runtime. The decision's real guarantee is a compile-time one — <c>logBreak.DriverId
/// == caller.DriverId</c> must not compile when one side resolved a user id — and a test cannot
/// assert that a program failed to compile. What it can assert is every property the compiler
/// relies on: three separate value types, no conversion between any pair of them, and value
/// equality within each. Remove any one and the compile-time claim quietly stops holding.
/// </summary>
public class TypedIdentityTests
{
    private static readonly Type[] IdentityKinds = [typeof(UserId), typeof(DriverId), typeof(ClientId)];

    [Theory]
    [InlineData(typeof(UserId))]
    [InlineData(typeof(DriverId))]
    [InlineData(typeof(ClientId))]
    public void Identity_kind_is_a_readonly_record_struct(Type type)
    {
        Assert.True(type.IsValueType, $"{type.Name} must be a struct, not a class.");

        Assert.Contains(
            type.GetCustomAttributes(inherit: false),
            attribute => attribute is IsReadOnlyAttribute);

        // PrintMembers is the marker the compiler emits for a record, and Deconstruct the one it
        // emits for a positional one. Together they say "record struct" without a source scan.
        Assert.NotNull(type.GetMethod(
            "PrintMembers",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

        Assert.NotNull(type.GetMethod("Deconstruct", BindingFlags.Instance | BindingFlags.Public));

        Assert.Contains(
            type.GetInterfaces(),
            contract => contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IEquatable<>)
                && contract.GetGenericArguments()[0] == type);
    }

    [Theory]
    [InlineData(typeof(UserId))]
    [InlineData(typeof(DriverId))]
    [InlineData(typeof(ClientId))]
    public void Identity_kind_declares_no_conversion_at_all(Type type)
    {
        // Not "no conversion to the other two" but none whatsoever: an implicit conversion to
        // int would restore exactly the interchangeability AD-22 removes, by way of one hop.
        var conversions = type
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name is "op_Implicit" or "op_Explicit")
            .Select(method => method.ToString())
            .ToArray();

        Assert.Empty(conversions);
    }

    [Fact]
    public void The_three_kinds_are_distinct_types()
    {
        Assert.Equal(IdentityKinds.Length, IdentityKinds.Distinct().Count());
    }

    [Fact]
    public void Identity_kinds_compare_by_value_within_their_own_type()
    {
        Assert.Equal(new UserId(7), new UserId(7));
        Assert.NotEqual(new UserId(7), new UserId(8));
        Assert.Equal(new UserId(7).GetHashCode(), new UserId(7).GetHashCode());

        Assert.Equal(new DriverId(7), new DriverId(7));
        Assert.Equal(new ClientId(7), new ClientId(7));
    }

    [Fact]
    public void Same_number_in_two_kinds_is_not_the_same_value()
    {
        // The whole threat in one line: these two carry the number 7 and are not equal, so a
        // comparison that reaches for the wrong one cannot accidentally succeed.
        object driver = new DriverId(7);
        object user = new UserId(7);

        Assert.NotEqual(driver, user);
    }

    [Fact]
    public void Identity_kind_carries_the_number_it_was_given()
    {
        Assert.Equal(42, new UserId(42).Value);
        Assert.Equal(42, new DriverId(42).Value);
        Assert.Equal(42, new ClientId(42).Value);
    }
}
