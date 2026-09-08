using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DriveTrack.Application;
using DriveTrack.Application.Authorization;

namespace DriveTrack.Application.Tests.Authorization;

/// <summary>
/// AD-2, as a build-failing test.
/// <para>
/// The decision this suite exists to protect is that <em>every</em> public method on an Application
/// service takes an authorization decision, and that the exceptions are a closed list somebody had
/// to write down. Reviews do not catch a missing guard call: it is an absence, and absences are
/// invisible in a diff. Reflection cannot catch it either — a method's signature says nothing about
/// what it calls — so this reads the compiled IL and looks for the call itself.
/// </para>
/// <para>
/// The async detour matters. An <c>async</c> method's own body is three instructions that start a
/// state machine; the code a developer wrote lives in the generated type's <c>MoveNext</c>. Scanning
/// the declared method alone would find no guard call anywhere and pass every service in the system.
/// </para>
/// </summary>
public class GuardCoverageTests
{
    /// <summary>Every opcode by its numeric value, so the IL walk knows how long each instruction is.</summary>
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => field.GetValue(null))
        .OfType<OpCode>()
        .ToDictionary(opCode => opCode.Value);

    /// <summary>
    /// The application services this rule is about: a type whose own name ends in <c>Service</c>, or
    /// one implementing an Application interface whose name does.
    /// <para>
    /// The second clause is what stops the gate being a naming convention. A capability class called
    /// anything else would carry no AD-2 obligation at all under a name-only rule, and the allowlist
    /// test could not notice, because a method it never scanned is not a method it can miss.
    /// </para>
    /// </summary>
    private static readonly Type[] Services = ApplicationAssembly.Assembly
        .GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false })
        .Where(IsService)
        .OrderBy(type => type.Name, StringComparer.Ordinal)
        .ToArray();

    [Fact]
    public void There_are_application_services_to_scan()
    {
        // Guards every assertion below: an empty set makes them all vacuously true, which is exactly
        // how a coverage test quietly stops covering anything.
        Assert.NotEmpty(Services);
    }

    [Fact]
    public void Every_public_service_method_calls_the_guard_or_is_an_allowlisted_entry_point()
    {
        var offenders = new List<string>();

        foreach (var service in Services)
        {
            foreach (var method in PublicMethodsOf(service))
            {
                var name = service.Name + "." + method.Name;

                if (PublicEntryPoints.Methods.Contains(name))
                {
                    continue;
                }

                if (!CallsTheGuard(method))
                {
                    offenders.Add(name);
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_allowlisted_entry_point_resolves_to_a_method()
    {
        // The other direction. Without it the allowlist rots into a list of names that used to mean
        // something, and a renamed method silently loses its guard requirement rather than gaining
        // one back.
        var declared = Services
            .SelectMany(service => PublicMethodsOf(service).Select(method => service.Name + "." + method.Name))
            .ToHashSet(StringComparer.Ordinal);

        var dangling = PublicEntryPoints.Methods
            .Where(entry => !declared.Contains(entry))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(dangling);
    }

    [Fact]
    public void The_allowlist_is_exactly_registration_and_sign_in()
    {
        // The list is meant to be short and argued for, not a place things accumulate. Pinning it
        // makes adding a third public operation a deliberate edit to this line.
        Assert.Equal(
            ["UserService.RegisterClientAsync", "UserService.SignInAsync"],
            PublicEntryPoints.Methods.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_method_with_no_guard_call_is_actually_detected()
    {
        // A coverage test that cannot fail is worthless, so the detector is pointed at a method that
        // provably does not call the guard. Without this, a bug in the IL walk would make every
        // assertion above pass for the wrong reason.
        var unguarded = typeof(GuardCoverageProbe).GetMethod(nameof(GuardCoverageProbe.UnguardedAsync))!;
        var guarded = typeof(GuardCoverageProbe).GetMethod(nameof(GuardCoverageProbe.GuardedAsync))!;

        Assert.False(CallsTheGuard(unguarded));
        Assert.True(CallsTheGuard(guarded));
    }

    private static bool IsService(Type type) =>
        type.Name.EndsWith("Service", StringComparison.Ordinal)
        || type.GetInterfaces().Any(contract =>
            contract.Assembly == ApplicationAssembly.Assembly
            && contract.Name.EndsWith("Service", StringComparison.Ordinal));

    /// <summary>
    /// The public instance operations a service exposes, whether it declares them itself or inherits
    /// them. Property accessors and anything inherited from <see cref="object"/> are not operations
    /// and are not what AD-2 is about.
    /// <para>
    /// Inherited methods are included deliberately. <c>DeclaredOnly</c> would let a capability move
    /// its public method onto a base class and carry no AD-2 obligation at all - and the allowlist
    /// test could not notice, because a method it never scanned is not a method it can miss.
    /// </para>
    /// </summary>
    private static IEnumerable<MethodInfo> PublicMethodsOf(Type service) =>
        service
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => !method.IsSpecialName && method.DeclaringType != typeof(object))
            .OrderBy(method => method.Name, StringComparer.Ordinal);

    /// <summary>True when the method's body contains a call whose callee is declared on the guard.</summary>
    private static bool CallsTheGuard(MethodInfo method)
    {
        var target = Target(method);
        var body = target.GetMethodBody();

        if (body is null)
        {
            return false;
        }

        var il = body.GetILAsByteArray();

        if (il is null)
        {
            return false;
        }

        var typeArguments = target.DeclaringType?.IsGenericType == true
            ? target.DeclaringType.GetGenericArguments()
            : null;

        var methodArguments = target.IsGenericMethodDefinition
            ? target.GetGenericArguments()
            : null;

        foreach (var token in CallTokens(il))
        {
            MethodBase? callee;

            try
            {
                callee = target.Module.ResolveMethod(token, typeArguments, methodArguments);
            }
            catch (ArgumentException)
            {
                // A token this module cannot resolve is not a call to the guard, and a scan that
                // threw here would report a defect in itself as a defect in the service.
                continue;
            }

            if (callee?.DeclaringType == typeof(IAccessGuard))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The method whose IL actually holds the developer's code: the state machine's
    /// <c>MoveNext</c> for an async method, the method itself otherwise.
    /// </summary>
    private static MethodBase Target(MethodInfo method)
    {
        var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>();

        if (stateMachine is null)
        {
            return method;
        }

        return stateMachine.StateMachineType.GetMethod(
                   "MoveNext",
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
               ?? method;
    }

    /// <summary>
    /// Walks the instruction stream and yields the metadata token of every <c>call</c> and
    /// <c>callvirt</c>.
    /// <para>
    /// A real walk rather than a scan for byte patterns: <c>0x28</c> occurs inside operands all the
    /// time, and a pattern match would resolve garbage tokens and could just as easily report a
    /// guard call that is not there.
    /// </para>
    /// </summary>
    private static IEnumerable<int> CallTokens(byte[] il)
    {
        var offset = 0;

        while (offset < il.Length)
        {
            short value;

            if (il[offset] == 0xFE && offset + 1 < il.Length)
            {
                value = unchecked((short)(0xFE00 + il[offset + 1]));
                offset += 2;
            }
            else
            {
                value = il[offset];
                offset += 1;
            }

            if (!OpCodesByValue.TryGetValue(value, out var opCode))
            {
                yield break;
            }

            if ((opCode == OpCodes.Call || opCode == OpCodes.Callvirt) && offset + 4 <= il.Length)
            {
                yield return BitConverter.ToInt32(il, offset);
            }

            var operandSize = OperandSize(opCode, il, offset);

            if (operandSize < 0)
            {
                yield break;
            }

            offset += operandSize;
        }
    }

    private static int OperandSize(OpCode opCode, byte[] il, int offset) => opCode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget
            or OperandType.InlineField
            or OperandType.InlineI
            or OperandType.InlineMethod
            or OperandType.InlineSig
            or OperandType.InlineString
            or OperandType.InlineTok
            or OperandType.InlineType
            or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => offset + 4 <= il.Length
            ? 4 + (4 * BitConverter.ToInt32(il, offset))
            : -1,
        _ => -1,
    };
}

/// <summary>
/// Two methods with a known answer, so the detector above is itself tested. They live in the test
/// assembly and are not named <c>*Service</c>, so nothing here is scanned as a service.
/// </summary>
public sealed class GuardCoverageProbe(IAccessGuard guard)
{
    /// <summary>Calls the guard, awaiting either side of it so the state-machine detour is exercised.</summary>
    public async Task GuardedAsync(DriveTrack.Domain.Identity.UserId userId)
    {
        await Task.Yield();

        guard.RequireSelf(userId);
    }

    /// <summary>Deliberately does not call the guard.</summary>
    public async Task UnguardedAsync(DriveTrack.Domain.Identity.UserId userId)
    {
        await Task.Yield();

        GC.KeepAlive(userId);
    }
}
