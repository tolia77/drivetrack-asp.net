using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DriveTrack.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace DriveTrack.Infrastructure.Objects;

/// <summary>
/// <see cref="IAssetStore"/> over Garage's S3 interface.
/// <para>
/// Three settings on the client are not preferences, and leaving any of them at an AWS default
/// produces a 403 that mentions none of them:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>ServiceURL</c>, because without it the SDK resolves an Amazon endpoint and the request never
/// reaches the node at all.
/// </description></item>
/// <item><description>
/// <c>ForcePathStyle</c>, because the virtual-hosted form puts the bucket in the host name, and
/// <c>drivetrack-proofs.objects</c> is a name nothing resolves.
/// </description></item>
/// <item><description>
/// <c>AuthenticationRegion</c>, because SigV4 signs over the region: a signature computed for
/// <c>us-east-1</c> against a node that expects <c>garage</c> does not match, and the answer is a
/// refusal that says nothing about regions.
/// </description></item>
/// </list>
/// <para>
/// The upload is buffered into a <see cref="MemoryStream"/> first and sent with chunk encoding off,
/// so Garage receives an ordinary signed body of a known length rather than the
/// <c>aws-chunked</c> streaming form the SDK prefers for a non-seekable stream. Buffering is
/// affordable precisely because <c>ProofAssetRules</c> refused anything above its per-asset cap
/// before this adapter was reached.
/// </para>
/// </summary>
internal sealed class GarageAssetStore : IAssetStore, IDisposable
{
    private readonly ObjectStoreOptions _options;
    private readonly IAmazonS3? _client;

    /// <summary>Builds the client, or none at all when no endpoint is configured.</summary>
    /// <param name="options">The bound settings.</param>
    public GarageAssetStore(IOptions<ObjectStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;

        // No endpoint at all means no store: the key was left out, which is the one way a
        // deployment says it has none (a present-but-blank value never reaches here - startup
        // refuses it). Constructed as null rather than as a client pointed at nothing, so the
        // failure is the message below rather than an SDK exception naming an Amazon host nobody
        // configured.
        if (string.IsNullOrWhiteSpace(_options.ServiceUrl))
        {
            return;
        }

        var configuration = new AmazonS3Config
        {
            ServiceURL = _options.ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = _options.Region,
        };

        _client = new AmazonS3Client(
            new BasicAWSCredentials(_options.AccessKey, _options.SecretKey),
            configuration);
    }

    /// <inheritdoc />
    public async Task<string> SaveAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        var client = Configured();

        // The key is minted here and nowhere else, which is what makes it opaque above this layer:
        // a GUID carries no delivery id, no driver and no date, so a key that escaped into a log
        // discloses nothing and a key cannot be guessed from another one. The extension is a
        // courtesy to whoever browses the bucket - nothing reads it back, because the content type
        // travels on the row (DR-14).
        var key = "proof/" + Guid.NewGuid().ToString("N") + ExtensionFor(contentType);

        // Buffered so the request carries Content-Length and a signature over the whole body.
        using var buffer = new MemoryStream();

        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        var request = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = buffer,
            ContentType = contentType,

            // Off deliberately: with it on the SDK sends the body in `aws-chunked` frames with a
            // per-chunk signature, which is a form of the protocol this store does not need and
            // which turns a buffered five-megabyte upload into something harder to read on the wire
            // than it is to send.
            UseChunkEncoding = false,
        };

        // Nothing is caught. The port's contract is that a failed save throws, so that no row is
        // ever committed against a key the store does not hold - the opposite of the geocoder's
        // contract, and for the opposite reason.
        await client.PutObjectAsync(request, cancellationToken);

        return key;
    }

    /// <inheritdoc />
    public async Task<Stream?> OpenAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var client = Configured();

        try
        {
            var response = await client.GetObjectAsync(
                new GetObjectRequest { BucketName = _options.Bucket, Key = key },
                cancellationToken);

            // The response is not disposed here on purpose: the stream it carries is the return
            // value, and disposing the response would close it. Ownership passes to the caller,
            // and disposing the stream is what releases the connection underneath it.
            return response.ResponseStream;
        }
        catch (AmazonS3Exception failure) when (IsMissing(failure))
        {
            // A key the store does not hold is null rather than an exception: the row can outlive
            // its object if a bucket is emptied out of band, and the caller turns that into a 404.
            // Every other failure - unreachable, refused, malformed - is left to throw, because
            // those are not "there is no such image".
            return null;
        }
    }

    /// <summary>
    /// Releases the S3 client and the connection pool it holds.
    /// <para>
    /// This type owns the client — it constructs it rather than being handed one — so it is the
    /// thing that has to let go of it. It is registered as a singleton, so in the application this
    /// runs once at shutdown; in a test that builds a provider per case it is what stops each one
    /// leaving a pool behind for a finalizer to find.
    /// </para>
    /// </summary>
    public void Dispose() => _client?.Dispose();

    /// <summary>
    /// The client, or a refusal naming the key an operator would search for. Written the way
    /// <c>SmtpEmailSender</c> writes the same case: an unconfigured port must say so rather than
    /// fail somewhere further in with a message about somebody else's service.
    /// </summary>
    /// <exception cref="InvalidOperationException">No endpoint is configured.</exception>
    private IAmazonS3 Configured() =>
        _client ?? throw new InvalidOperationException(
            $"No object store is configured. Set the '{ObjectStoreOptions.SectionName}__ServiceUrl' "
                + "environment variable (see .env.example) before capturing a proof of delivery.");

    /// <summary>
    /// Whether a refusal means "no such object". Both shapes are checked: the code is what a
    /// well-formed S3 error carries, and the status is what arrives when the error document is
    /// absent or is not the shape the SDK expected.
    /// </summary>
    private static bool IsMissing(AmazonS3Exception failure) =>
        failure.StatusCode == System.Net.HttpStatusCode.NotFound
        || string.Equals(failure.ErrorCode, "NoSuchKey", StringComparison.Ordinal);

    /// <summary>
    /// The file extension for a stored type. Total over the allowlist rather than over every MIME
    /// type there is: the validator has already refused anything else, so the empty default is
    /// unreachable rather than a fallback anything depends on.
    /// </summary>
    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        _ => string.Empty,
    };
}
