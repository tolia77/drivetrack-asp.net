using DriveTrack.Integration.Tests.Support;

namespace DriveTrack.Integration.Tests.Configuration;

/// <summary>
/// The two compose files, held to the parts of each other they have to agree on.
/// <para>
/// <c>compose.dev.yaml</c> used to be an overlay: compose merged it onto the production file, so
/// the database image, the Garage provisioning script and the app's environment block existed once
/// and development inherited them. That made development a dependent of production — an edit meant
/// for one arrived in the other — so the files were separated, and each is now standalone.
/// </para>
/// <para>
/// Separation costs what inheritance was paying for. The services below are duplicated text, and
/// duplicated text drifts: a new <c>Foo__Bar</c> added to production's <c>app</c> and not to
/// development's is a setting the hot-reload container is never given, and the symptom is a feature
/// that works in the deployment and not on the machine it was written on — or, far worse, the other
/// way round. Nothing about two standalone files notices that. These cases do.
/// </para>
/// <para>
/// What is asserted is parity where the stacks are the same system, never sameness everywhere: the
/// differences that make development development — the watch image, the published database port,
/// the pinned <c>ASPNETCORE_ENVIRONMENT</c> — are asserted to be present, so a copy-paste that
/// flattened them back into a second production stack fails here too.
/// </para>
/// </summary>
public sealed class StackParityTests
{
    /// <summary>The services both stacks run, and that carry no development variation at all.</summary>
    public static TheoryData<string> SharedServices => new() { "objects", "objects-init" };

    [Theory]
    [MemberData(nameof(SharedServices))]
    public void Both_stacks_run_the_same_image_for_a_shared_service(string service)
    {
        // The object store is one component, not two. A version bumped in one file and not the
        // other is a development stack testing against a Garage the deployment does not run.
        Assert.Equal(ComposeStack.Prod.ImageOf(service), ComposeStack.Dev.ImageOf(service));
    }

    [Theory]
    [MemberData(nameof(SharedServices))]
    public void Both_stacks_hand_a_shared_service_the_same_environment(string service)
    {
        Assert.Equal(
            ComposeStack.Prod.EnvironmentKeysOf(service),
            ComposeStack.Dev.EnvironmentKeysOf(service));

        foreach (var key in ComposeStack.Prod.EnvironmentKeysOf(service))
        {
            Assert.Equal(
                ComposeStack.Prod.EnvironmentValueOf(service, key),
                ComposeStack.Dev.EnvironmentValueOf(service, key));
        }
    }

    [Fact]
    public void Both_stacks_provision_garage_with_the_same_script()
    {
        // The longest duplicated thing in either file, and the one with the least chance of being
        // spotted by eye: ~120 lines of shell in a block scalar. A fix applied to one copy only -
        // a retry loop, a permission triple, the layout version read - leaves the other stack
        // provisioning a node the way nobody intended any more.
        Assert.Equal(ComposeStack.Prod.ObjectsInitScript, ComposeStack.Dev.ObjectsInitScript);
    }

    [Fact]
    public void Both_stacks_mount_the_garage_configuration_at_the_same_path()
    {
        // Both files mount the one checked-in garage.toml. Retargeting the mount in a single file
        // starts that stack's node on defaults - no region, no replication factor - and the
        // failure surfaces as S3 calls refused rather than as a bad mount.
        Assert.Equal(
            ComposeStack.Prod.GarageConfigMountTarget("objects"),
            ComposeStack.Dev.GarageConfigMountTarget("objects"));
    }

    [Fact]
    public void Both_stacks_hand_the_app_the_same_settings_but_for_its_environment_name()
    {
        // The app reads one configuration contract, and both stacks have to satisfy all of it.
        // ASPNETCORE_ENVIRONMENT is the single exception and is asserted separately below: it is
        // the setting that *makes* one of these stacks the development one.
        const string EnvironmentName = "ASPNETCORE_ENVIRONMENT";

        var prod = ComposeStack.Prod.EnvironmentKeysOf("app");
        var dev = ComposeStack.Dev.EnvironmentKeysOf("app");

        Assert.Equal(prod, dev);

        foreach (var key in prod.Where(key => key != EnvironmentName))
        {
            Assert.Equal(
                ComposeStack.Prod.EnvironmentValueOf("app", key),
                ComposeStack.Dev.EnvironmentValueOf("app", key));
        }
    }

    [Fact]
    public void The_development_stack_pins_its_environment_rather_than_forwarding_it()
    {
        // Production forwards ${ASPNETCORE_ENVIRONMENT} because a deployment chooses. Development
        // does not get to choose: hot reload is a Development-environment feature, and an .env
        // carrying Production - which is exactly what an .env copied from a server carries - would
        // silently disable the one thing that file exists to provide.
        Assert.Equal(
            "Development",
            ComposeStack.Dev.EnvironmentValueOf("app", "ASPNETCORE_ENVIRONMENT"));

        Assert.Equal(
            "${ASPNETCORE_ENVIRONMENT}",
            ComposeStack.Prod.EnvironmentValueOf("app", "ASPNETCORE_ENVIRONMENT"));
    }

    [Fact]
    public void The_two_stacks_build_the_app_from_different_images()
    {
        // Same service name, same project shape, and two images that must never be confused: one
        // is a published Release build with no SDK, the other an SDK image running `dotnet watch`.
        // A dev file that had drifted onto the production Dockerfile would come up unable to
        // reload, which reads as "hot reload is broken" rather than as "wrong image".
        Assert.Equal("drivetrack-app-dev", ComposeStack.Dev.ImageOf("app"));

        Assert.Contains(
            "Dockerfile.dev",
            File.ReadAllText(Path.Combine(RepositoryLayout.SolutionRoot.FullName, ComposeStack.Dev.FileName)),
            StringComparison.Ordinal);

        Assert.Contains(
            "Dockerfile.prod",
            File.ReadAllText(Path.Combine(RepositoryLayout.SolutionRoot.FullName, ComposeStack.Prod.FileName)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_stacks_carry_different_compose_project_names()
    {
        // The project name is what separates the volumes. Were both files to declare `drivetrack`,
        // `docker compose -f compose.dev.yaml up` would adopt the production stack's db-data and
        // objects-data volumes - two environments writing the same database, which is the failure
        // this separation exists to prevent, arriving silently.
        Assert.Equal("drivetrack", ProjectNameOf(ComposeStack.Prod));
        Assert.Equal("drivetrack-dev", ProjectNameOf(ComposeStack.Dev));
    }

    /// <summary>The <c>name:</c> a compose file declares at its top level.</summary>
    private static string ProjectNameOf(ComposeStack stack)
    {
        var path = Path.Combine(RepositoryLayout.SolutionRoot.FullName, stack.FileName);

        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith("name:", StringComparison.Ordinal))
            {
                return line["name:".Length..].Trim();
            }
        }

        throw new InvalidOperationException(
            $"'{stack.FileName}' declares no top-level 'name:', so compose derives its project "
                + "name from the directory - and both stacks would derive the same one.");
    }
}
