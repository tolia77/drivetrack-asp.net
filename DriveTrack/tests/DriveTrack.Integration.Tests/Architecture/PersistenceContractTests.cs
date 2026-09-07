using System.Reflection;
using DriveTrack.Application;
using DriveTrack.Application.Abstractions;
using DriveTrack.Infrastructure;
using DriveTrack.Infrastructure.Persistence;
using DriveTrack.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Integration.Tests.Architecture;

/// <summary>
/// The persistence contract AD-5 and AD-6 fix, asserted from metadata and source text rather
/// than from a database — these are rules about shape, and a rule about shape that is only
/// checked by reading the diff is a rule that lasts until the first hurried afternoon.
/// </summary>
public class PersistenceContractTests
{
    private const string ValidConnectionString =
        "Host=localhost;Port=5432;Database=drivetrack;Username=drivetrack;Password=irrelevant";

    /// <summary>
    /// The one file outside <c>Persistence/</c> that may name <c>AppDbContext</c>:
    /// Infrastructure's single composition surface, which AD-1 makes the only place permitted
    /// to register it. Matched on the full path under <c>src/</c>, not on the bare file name -
    /// a <c>DriveTrack.Web/DependencyInjection.cs</c> is precisely what this test exists to
    /// catch, and a name-only match would wave it through.
    /// </summary>
    private const string CompositionSurface = "DriveTrack.Infrastructure/DependencyInjection.cs";

    /// <summary>Where the context lives. Same reasoning: the whole path, not any segment.</summary>
    private const string PersistenceFolder = "DriveTrack.Infrastructure/Persistence/";

    private static readonly Type[] Abstractions =
        typeof(ApplicationAssembly).Assembly
            .GetTypes()
            .Where(type => type.IsInterface
                && type.Namespace == "DriveTrack.Application.Abstractions")
            .OrderBy(type => type.Name)
            .ToArray();

    [Fact]
    public void There_is_an_abstraction_surface_to_assert_against()
    {
        // Guards every other test in this class: an empty set makes them all vacuously true.
        Assert.NotEmpty(Abstractions);
    }

    [Fact]
    public void No_abstraction_returns_an_IQueryable()
    {
        // AD-6: an IQueryable crossing this boundary carries a live context with it, and from
        // then on "which layer owns this query" is a per-screen judgement call.
        var offenders = Abstractions
            .SelectMany(type => type.GetMethods())
            .Where(method => IsQueryable(method.ReturnType))
            .Select(method => $"{method.DeclaringType?.Name}.{method.Name}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_abstraction_returns_a_lazy_sequence()
    {
        // The other half of AD-6: IAsyncEnumerable streams over an open reader, so a result that
        // "survives disposal" would depend on the caller having enumerated it in time.
        var offenders = Abstractions
            .SelectMany(type => type.GetMethods())
            .Where(method => IsAsyncEnumerable(method.ReturnType))
            .Select(method => $"{method.DeclaringType?.Name}.{method.Name}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_asynchronous_abstraction_method_takes_a_cancellation_token()
    {
        var offenders = Abstractions
            .SelectMany(type => type.GetMethods())
            .Where(method => IsAsynchronous(method.ReturnType))
            .Where(method => !method.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(CancellationToken)))
            .Select(method => $"{method.DeclaringType?.Name}.{method.Name}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Timeline_repository_exposes_no_way_to_change_an_entry()
    {
        // AD-27 is enforced by the absence of the method as much as by the trigger: a path that
        // does not exist cannot be taken by mistake.
        var offenders = typeof(ITimelineEntryRepository)
            .GetMembers()
            .Where(member => member.Name.Contains("Update", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("Remove", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase))
            // ListForDeliveryAsync names a delivery, not a deletion.
            .Where(member => !member.Name.StartsWith("ListForDelivery", StringComparison.Ordinal))
            .Select(member => member.Name)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_container_offers_the_context_only_through_its_factory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(), new TestHostEnvironment());

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(AppDbContext));
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IDbContextFactory<AppDbContext>));

        // AD-5's scope is reachable, so nothing has a reason to want the context itself.
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IUnitOfWorkFactory));
    }

    [Fact]
    public void The_container_registers_the_clock()
    {
        // AD-13: DateTime.UtcNow is untestable and database default timestamps are worse, so the
        // clock has to be resolvable before anything can be asked to use it.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(BuildConfiguration(), new TestHostEnvironment());

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(TimeProvider));
    }

    [Fact]
    public void No_source_file_outside_persistence_names_the_context()
    {
        var root = RepositoryLayout.Source.FullName;

        var offenders = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path, root))
            .Select(path => RelativePath(path, root))
            .Where(relative => !relative.StartsWith(PersistenceFolder, StringComparison.Ordinal))
            .Where(relative => !relative.Equals(CompositionSurface, StringComparison.Ordinal))
            .Where(relative => File.ReadAllText(Path.Combine(root, relative))
                .Contains("AppDbContext", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_source_file_reads_the_ambient_clock()
    {
        // AD-13's other half. The registration above only proves the clock is resolvable; this
        // proves nothing bypasses it. Comment lines are skipped so the prose explaining the rule
        // is not mistaken for a breach of it.
        var root = RepositoryLayout.Source.FullName;

        var offenders = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path, root))
            .Where(path => File.ReadLines(path).Any(ReadsTheAmbientClock))
            .Select(path => RelativePath(path, root))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static bool IsQueryable(Type type) =>
        type == typeof(IQueryable)
        || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>))
        || (type.IsGenericType
            && type.GetGenericArguments().Any(IsQueryable));

    private static bool IsAsyncEnumerable(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>);

    private static bool IsAsynchronous(Type type) =>
        type == typeof(Task)
        || type == typeof(ValueTask)
        || (type.IsGenericType
            && (type.GetGenericTypeDefinition() == typeof(Task<>)
                || type.GetGenericTypeDefinition() == typeof(ValueTask<>)));

    /// <summary>
    /// True when a line of source reads the clock directly rather than through the injected
    /// <see cref="TimeProvider"/>. <c>DateTimeOffset</c> is included alongside <c>DateTime</c>
    /// because every stored timestamp is a <c>DateTimeOffset</c>, so that is the shape an
    /// ambient read would actually take here.
    /// </summary>
    private static bool ReadsTheAmbientClock(string line)
    {
        var code = line.TrimStart();
        if (code.StartsWith("//", StringComparison.Ordinal)
            || code.StartsWith("*", StringComparison.Ordinal))
        {
            return false;
        }

        return code.Contains("DateTime.UtcNow", StringComparison.Ordinal)
            || code.Contains("DateTime.Now", StringComparison.Ordinal)
            || code.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal)
            || code.Contains("DateTimeOffset.Now", StringComparison.Ordinal);
    }

    /// <summary>Path under <c>src/</c>, with forward slashes so the comparisons above read the same on any host.</summary>
    private static string RelativePath(string path, string root) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsBuildOutput(string path, string root)
    {
        var segments = Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ValidConnectionString,
            })
            .Build();
}
