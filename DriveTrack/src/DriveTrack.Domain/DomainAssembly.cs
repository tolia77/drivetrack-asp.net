namespace DriveTrack.Domain;

/// <summary>
/// Marker type used to resolve the Domain assembly from compiled metadata.
/// AD-1's layering test asserts against this assembly's references, so the assertion
/// is compile-checked rather than driven by an assembly-name string.
/// </summary>
public static class DomainAssembly
{
    /// <summary>The assembly that carries the Domain layer.</summary>
    public static System.Reflection.Assembly Assembly => typeof(DomainAssembly).Assembly;
}
