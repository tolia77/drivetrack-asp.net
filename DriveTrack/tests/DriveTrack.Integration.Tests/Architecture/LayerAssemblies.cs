using System.Reflection;

namespace DriveTrack.Integration.Tests.Architecture;

/// <summary>
/// Resolves each layer's assembly through a type rather than an assembly-name string, so a
/// renamed or deleted layer breaks the compile instead of silently passing the test.
/// </summary>
internal static class LayerAssemblies
{
    private static readonly Dictionary<string, Assembly> ByName = new()
    {
        ["DriveTrack.Domain"] = typeof(DriveTrack.Domain.DomainAssembly).Assembly,
        ["DriveTrack.Application"] = typeof(DriveTrack.Application.ApplicationAssembly).Assembly,
        ["DriveTrack.Infrastructure"] = typeof(DriveTrack.Infrastructure.DependencyInjection).Assembly,
        ["DriveTrack.Web"] = typeof(DriveTrack.Web.Components.App).Assembly,
    };

    public static Assembly Resolve(string projectName) => ByName[projectName];
}
