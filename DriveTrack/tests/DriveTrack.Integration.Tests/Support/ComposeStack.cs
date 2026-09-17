namespace DriveTrack.Integration.Tests.Support;

/// <summary>
/// One checked-in compose file, read as data.
/// <para>
/// A harness that stood a Garage node up from constants of its own would assert that
/// <em>something</em> works, not that the deployment works: the image could move, the provisioning
/// script could stop creating the bucket, and the suite would stay green because it never read
/// either. So the image names, the provisioning script, the node configuration and the
/// bucket/key/zone/capacity values all come from the compose files, <c>garage.toml</c> and
/// <c>.env.example</c>, and drift in any of them changes what the tests run.
/// </para>
/// <para>
/// There is an instance per stack because there are two stacks and neither is derived from the
/// other — <see cref="Prod"/> is what deploys and <see cref="Dev"/> is what a developer runs. Most
/// of the suite reads <see cref="Prod"/>, because that is the deliverable; what reads both is
/// <c>StackParityTests</c>, which is the only thing standing between two standalone files and
/// silent divergence.
/// </para>
/// <para>
/// Every accessor throws rather than answering an empty string. A missing service, a missing image
/// line or a renamed <c>.env.example</c> key is a change somebody made to the deployment, and the
/// useful failure names the file and the thing that was not in it.
/// </para>
/// </summary>
internal sealed class ComposeStack
{
    private const string GarageConfigFile = "garage.toml";
    private const string EnvExampleFile = ".env.example";

    /// <summary>Indent of a service key in compose: <c>services:</c> is 0, a service name is 2.</summary>
    private const int ServiceIndent = 2;

    /// <summary>Indent of a service's own settings — <c>image:</c>, <c>command:</c>, <c>entrypoint:</c>.</summary>
    private const int SettingIndent = 4;

    private static readonly string[] EnvExampleLines = ReadLines(EnvExampleFile);

    private readonly string composeFile;

    private readonly string[] composeLines;

    private ComposeStack(string composeFile)
    {
        this.composeFile = composeFile;
        composeLines = ReadLines(composeFile);
        ObjectsInitScript = ReadObjectsInitScript();
    }

    /// <summary>The production stack — the file a deployment brings up.</summary>
    public static ComposeStack Prod { get; } = new("compose.prod.yaml");

    /// <summary>The development stack — standalone, and not an overlay on <see cref="Prod"/>.</summary>
    public static ComposeStack Dev { get; } = new("compose.dev.yaml");

    /// <summary>The file this instance reads, for naming in a failure message.</summary>
    public string FileName => composeFile;

    /// <summary>
    /// The provisioning script <c>objects-init</c> runs, dedented out of its block scalar and with
    /// compose's <c>$$</c> interpolation escape turned back into a shell <c>$</c>.
    /// </summary>
    public string ObjectsInitScript { get; }

    /// <summary><c>garage.toml</c> verbatim, for mapping into the node where compose mounts it.</summary>
    public static byte[] GarageConfigBytes { get; } = ReadGarageConfig();

    /// <summary>
    /// The path a compose service mounts <c>garage.toml</c> at.
    /// <para>
    /// Compose's path rather than one of ours: the node takes its region, its ports and its
    /// replication factor from whatever lands there, so a mount deleted or retargeted in the
    /// compose file has to move the file here too. Restated as a literal, it would let a
    /// <c>docker compose up</c> that starts a node with no configuration at all stay green.
    /// </para>
    /// </summary>
    /// <param name="service">The service name, as it appears under <c>services:</c>.</param>
    /// <exception cref="InvalidOperationException">The service mounts no <c>garage.toml</c>.</exception>
    public string GarageConfigMountTarget(string service) =>
        MountTargetOf(service, "./" + GarageConfigFile);

    /// <summary>The image a compose service runs.</summary>
    /// <param name="service">The service name, as it appears under <c>services:</c>.</param>
    /// <exception cref="InvalidOperationException">No such service, or it declares no image.</exception>
    public string ImageOf(string service)
    {
        foreach (var line in ServiceBlock(service))
        {
            if (IndentOf(line) != SettingIndent)
            {
                continue;
            }

            var setting = line.TrimStart();

            if (setting.StartsWith("image:", StringComparison.Ordinal))
            {
                var image = setting["image:".Length..].Trim();

                if (image.Length == 0)
                {
                    break;
                }

                return image;
            }
        }

        throw new InvalidOperationException(
            $"Service '{service}' in '{composeFile}' declares no image.");
    }

    /// <summary>The argument vector a compose service's <c>command:</c> declares, in flow form.</summary>
    /// <param name="service">The service name, as it appears under <c>services:</c>.</param>
    /// <exception cref="InvalidOperationException">No such service, or no flow-form command.</exception>
    public string[] CommandOf(string service) => FlowSequence(service, "command");

    /// <summary>The argument vector a compose service's <c>entrypoint:</c> declares.</summary>
    /// <param name="service">The service name, as it appears under <c>services:</c>.</param>
    /// <exception cref="InvalidOperationException">No such service, or no flow-form entrypoint.</exception>
    public string[] EntrypointOf(string service) => FlowSequence(service, "entrypoint");

    /// <summary>
    /// The environment variable names a compose service declares, in the order it declares them.
    /// <para>
    /// The <em>names</em> rather than the values: compose reads the values from an <c>.env</c> the
    /// repository deliberately does not carry, so the values come from <c>.env.example</c>. What
    /// this pins is the list — a key deleted or renamed in compose is a key the container here stops
    /// being given, which is what turns a broken <c>docker compose up</c> into a red test.
    /// </para>
    /// </summary>
    /// <param name="service">The service name, as it appears under <c>services:</c>.</param>
    /// <exception cref="InvalidOperationException">No such service, or it declares no environment.</exception>
    public IReadOnlyList<string> EnvironmentKeysOf(string service) =>
        EnvironmentOf(service).Select(entry => entry.Key).ToArray();

    /// <summary>
    /// The interpolation a compose service declares for one environment key — the right-hand side
    /// verbatim, <c>${Jwt__Issuer:-DriveTrack}</c> and all.
    /// <para>
    /// The <em>value</em> rather than only the name, because for a key with a working default the
    /// difference between <c>${X}</c> and <c>${X:-default}</c> is the difference between a container
    /// that starts and one that does not: compose forwards a bare reference to an unset variable as
    /// the empty string, and the app refuses a blank setting. A test that read only the names could
    /// not tell those two lines apart.
    /// </para>
    /// </summary>
    /// <param name="service">The service name, as it appears under <c>services:</c>.</param>
    /// <param name="key">The environment variable name, in its double-underscore spelling.</param>
    /// <exception cref="InvalidOperationException">
    /// No such service, it declares no environment, or that block names no such key.
    /// </exception>
    public string EnvironmentValueOf(string service, string key)
    {
        foreach (var entry in EnvironmentOf(service))
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        throw new InvalidOperationException(
            $"Service '{service}' in '{composeFile}' declares no '{key}' in its 'environment:' "
                + "block, so a container brought up from this file is never given it.");
    }

    /// <summary>The whole <c>environment:</c> block of one service, in declaration order.</summary>
    private IReadOnlyList<KeyValuePair<string, string>> EnvironmentOf(string service)
    {
        var entries = new List<KeyValuePair<string, string>>();
        var inside = false;

        foreach (var line in ServiceBlock(service))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            // A line back at the setting indent either opens the environment block or closes it.
            if (IndentOf(line) == SettingIndent)
            {
                if (inside)
                {
                    break;
                }

                inside = line.TrimStart().StartsWith("environment:", StringComparison.Ordinal);
                continue;
            }

            if (!inside)
            {
                continue;
            }

            var entry = line.TrimStart();

            if (entry.StartsWith('#'))
            {
                continue;
            }

            var colon = entry.IndexOf(':');

            if (colon <= 0)
            {
                throw new InvalidOperationException(
                    $"'{composeFile}' holds a line under '{service}'s 'environment:' that names no "
                        + $"key: '{line}'.");
            }

            entries.Add(new KeyValuePair<string, string>(entry[..colon], entry[(colon + 1)..].Trim()));
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException(
                $"Service '{service}' in '{composeFile}' declares no environment variables.");
        }

        return entries;
    }

    /// <summary>The value of a key in <c>.env.example</c>.</summary>
    /// <param name="key">The environment variable name, in its double-underscore spelling.</param>
    /// <exception cref="InvalidOperationException">
    /// The file declares no such key, or declares it blank. Blank is refused for the reason an
    /// absent key is: a container handed an empty secret fails somewhere further in, with a message
    /// about the daemon rather than about the line nobody filled in.
    /// </exception>
    public static string EnvExample(string key)
    {
        var prefix = key + "=";

        foreach (var line in EnvExampleLines)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[prefix.Length..].Trim();

            if (value.Length == 0)
            {
                throw new InvalidOperationException($"'{EnvExampleFile}' leaves '{key}' blank.");
            }

            return value;
        }

        throw new InvalidOperationException($"'{EnvExampleFile}' declares no '{key}'.");
    }

    /// <summary>
    /// Where a service mounts one of the repository's files, out of its <c>volumes:</c> block.
    /// </summary>
    /// <param name="service">The service name, as it appears under <c>services:</c>.</param>
    /// <param name="hostFile">The source side, spelled as compose spells it — <c>./garage.toml</c>.</param>
    private string MountTargetOf(string service, string hostFile)
    {
        var inside = false;

        foreach (var line in ServiceBlock(service))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            // A line back at the setting indent either opens the volumes block or closes it.
            if (IndentOf(line) == SettingIndent)
            {
                if (inside)
                {
                    break;
                }

                inside = line.TrimStart().StartsWith("volumes:", StringComparison.Ordinal);
                continue;
            }

            if (!inside)
            {
                continue;
            }

            var entry = line.TrimStart();

            if (entry.StartsWith('#'))
            {
                continue;
            }

            if (!entry.StartsWith("- ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"'{composeFile}' holds a line under '{service}'s 'volumes:' that is not a list "
                        + $"item: '{line}'.");
            }

            // `- <source>:<target>[:<mode>]`, the short form this file writes every mount in. A
            // named volume has no source path and simply does not match.
            var mount = entry[2..].Trim().Split(':');

            if (mount.Length >= 2 && string.Equals(mount[0], hostFile, StringComparison.Ordinal))
            {
                return mount[1];
            }
        }

        throw new InvalidOperationException(
            $"Service '{service}' in '{composeFile}' mounts no '{hostFile}'.");
    }

    /// <summary>
    /// A setting written as a single-line YAML flow sequence — <c>["/garage", "server"]</c>.
    /// </summary>
    private string[] FlowSequence(string service, string setting)
    {
        foreach (var line in ServiceBlock(service))
        {
            if (IndentOf(line) != SettingIndent)
            {
                continue;
            }

            var text = line.TrimStart();

            if (!text.StartsWith(setting + ":", StringComparison.Ordinal))
            {
                continue;
            }

            var value = text[(setting.Length + 1)..].Trim();

            if (!value.StartsWith('[') || !value.EndsWith(']'))
            {
                throw new InvalidOperationException(
                    $"'{setting}:' on service '{service}' in '{composeFile}' is no longer a "
                        + $"single-line [\"a\", \"b\"] sequence: '{value}'.");
            }

            var items = value[1..^1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.Trim('"', '\''))
                .ToArray();

            if (items.Length == 0)
            {
                throw new InvalidOperationException(
                    $"'{setting}:' on service '{service}' in '{composeFile}' is an empty sequence.");
            }

            return items;
        }

        throw new InvalidOperationException(
            $"Service '{service}' in '{composeFile}' declares no '{setting}:'.");
    }

    /// <summary>
    /// The lines of one service's block: everything under <c>  &lt;service&gt;:</c> up to the next
    /// line at that indent or shallower.
    /// </summary>
    private IEnumerable<string> ServiceBlock(string service)
    {
        var header = new string(' ', ServiceIndent) + service + ":";
        var start = Array.FindIndex(composeLines, line => line.TrimEnd() == header);

        if (start < 0)
        {
            throw new InvalidOperationException(
                $"'{composeFile}' declares no service '{service}'.");
        }

        for (var index = start + 1; index < composeLines.Length; index++)
        {
            var line = composeLines[index];

            // Blank lines belong to whichever block surrounds them; a non-blank line back at the
            // service indent is the next service, and the block has ended.
            if (line.Trim().Length != 0 && IndentOf(line) <= ServiceIndent)
            {
                yield break;
            }

            yield return line;
        }
    }

    /// <summary>
    /// Lifts the block scalar under <c>objects-init</c>'s <c>command:</c> out of the YAML.
    /// <para>
    /// The shape is <c>      - |</c> with the body indented deeper, so: find the marker, take every
    /// following line that is blank or indented deeper than it, stop at the first shallower non-blank
    /// line, strip the body's own indent, and undo compose's <c>$$</c> escape.
    /// </para>
    /// </summary>
    private string ReadObjectsInitScript()
    {
        const string Service = "objects-init";

        var block = ServiceBlock(Service).ToArray();

        var command = Array.FindIndex(
            block,
            line => IndentOf(line) == SettingIndent
                && line.TrimStart().StartsWith("command:", StringComparison.Ordinal));

        if (command < 0)
        {
            throw new InvalidOperationException(
                $"Service '{Service}' in '{composeFile}' declares no 'command:'.");
        }

        var marker = -1;

        for (var index = command + 1; index < block.Length; index++)
        {
            if (block[index].Trim().Length == 0)
            {
                continue;
            }

            marker = index;
            break;
        }

        if (marker < 0 || block[marker].Trim() != "- |")
        {
            throw new InvalidOperationException(
                $"Service '{Service}' in '{composeFile}' no longer holds its provisioning script "
                    + "in a single '- |' block scalar under 'command:'.");
        }

        var markerIndent = IndentOf(block[marker]);
        var body = new List<string>();

        for (var index = marker + 1; index < block.Length; index++)
        {
            var line = block[index];

            if (line.Trim().Length == 0)
            {
                body.Add(string.Empty);
                continue;
            }

            if (IndentOf(line) <= markerIndent)
            {
                break;
            }

            body.Add(line);
        }

        while (body.Count > 0 && body[^1].Length == 0)
        {
            body.RemoveAt(body.Count - 1);
        }

        if (body.Count == 0 || body[0].Length == 0)
        {
            throw new InvalidOperationException(
                $"The '- |' block under '{Service}'s 'command:' in '{composeFile}' is empty.");
        }

        // A block scalar's indentation is the indentation of its first non-empty line; every other
        // line carries at least that much, and what is left after stripping it is the script.
        var scalarIndent = IndentOf(body[0]);
        var script = new System.Text.StringBuilder();

        foreach (var line in body)
        {
            if (line.Length != 0)
            {
                if (IndentOf(line) < scalarIndent)
                {
                    throw new InvalidOperationException(
                        $"A line of the provisioning script in '{composeFile}' is indented less "
                            + $"than the {scalarIndent} spaces the block scalar opened with: '{line}'.");
                }

                script.Append(line[scalarIndent..]);
            }

            script.Append('\n');
        }

        // `$$` is compose's escape for a literal `$`; the shell that runs this script is not
        // compose, so it has to see what compose would have written.
        return script.ToString().Replace("$$", "$", StringComparison.Ordinal);
    }

    private static byte[] ReadGarageConfig()
    {
        var path = PathTo(GarageConfigFile);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"'{GarageConfigFile}' is not at '{path}'.");
        }

        return File.ReadAllBytes(path);
    }

    private static string[] ReadLines(string fileName)
    {
        var path = PathTo(fileName);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"'{fileName}' is not at '{path}'.");
        }

        return File.ReadAllLines(path);
    }

    private static string PathTo(string fileName) =>
        Path.Combine(RepositoryLayout.SolutionRoot.FullName, fileName);

    /// <summary>Leading spaces on a line. Blank lines are treated as infinitely deep.</summary>
    private static int IndentOf(string line)
    {
        var indent = 0;

        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        return indent == line.Length ? int.MaxValue : indent;
    }
}
