using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace DriveTrack.Integration.Tests.Support;

/// <summary>
/// A host environment for the tests that call <c>AddInfrastructure</c> without a host.
/// Production by default, because that is the branch where AD-19's diagnostics are off and the
/// one a test should have to opt out of deliberately.
/// </summary>
internal sealed class TestHostEnvironment : IHostEnvironment
{
    /// <inheritdoc />
    public string EnvironmentName { get; set; } = Environments.Production;

    /// <inheritdoc />
    public string ApplicationName { get; set; } = "DriveTrack.Tests";

    /// <inheritdoc />
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    /// <inheritdoc />
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
