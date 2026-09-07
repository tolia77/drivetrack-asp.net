using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace DriveTrack.Application;

/// <summary>
/// Application's composition surface, mirroring <c>AddInfrastructure</c>: the layer that owns the
/// validators is the layer that registers them, so no adapter has to know they exist.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers every <c>IValidator&lt;T&gt;</c> declared in this assembly (AD-9).
    /// <para>
    /// Discovery is from <see cref="ApplicationAssembly.Assembly"/>, the one assembly handle this
    /// layer exposes. Registration only makes a validator resolvable - it is still an application
    /// service that calls <c>ValidateAndThrowAsync</c>; nothing here hooks a pipeline.
    /// </para>
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddValidatorsFromAssembly(ApplicationAssembly.Assembly, includeInternalTypes: true);

        return services;
    }
}
