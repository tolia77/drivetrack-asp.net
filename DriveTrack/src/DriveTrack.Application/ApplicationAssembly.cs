namespace DriveTrack.Application;

/// <summary>
/// Marker type used to resolve the Application assembly from compiled metadata.
/// Exists for the same reason as <c>DomainAssembly</c>: AD-1's layering test resolves
/// the assembly through a type instead of a name.
/// </summary>
public static class ApplicationAssembly
{
    /// <summary>The assembly that carries the Application layer.</summary>
    public static System.Reflection.Assembly Assembly => typeof(ApplicationAssembly).Assembly;
}
