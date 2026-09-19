namespace DriveTrack.Infrastructure.Objects;

/// <summary>
/// The object-store settings, bound from the <c>ObjectStore</c> configuration section (AD-19).
/// <para>
/// Every value arrives as an environment variable in the container —
/// <c>ObjectStore__ServiceUrl</c>, <c>ObjectStore__Region</c>, <c>ObjectStore__AccessKey</c>,
/// <c>ObjectStore__SecretKey</c>, <c>ObjectStore__Bucket</c> — and each appears in
/// <c>.env.example</c> and is forwarded a line at a time in <c>compose.dev.yaml</c>.
/// </para>
/// <para>
/// <see cref="ServiceUrl"/> has no default, deliberately, and for the reason the geocoder's
/// endpoints have none: a committed default would point every environment that never configured one
/// at somebody else's service — and here that service would be Amazon S3, which is the default the
/// SDK falls back to when no endpoint is given. An <em>absent</em> key means "no object store is
/// configured", which the adapter answers by name at the first capture; a key that is present and
/// blank is a startup failure rather than that state, because blank cannot be told apart from a
/// deployment that meant to set an endpoint.
/// </para>
/// <para>
/// <see cref="Region"/> does have one, and it is <c>garage</c> rather than an AWS region name. It is
/// not a preference: SigV4 signs over the region, so a client that signs <c>us-east-1</c> against a
/// Garage node that expects <c>garage</c> gets a 403 that says nothing about regions at all.
/// </para>
/// </summary>
public sealed class ObjectStoreOptions
{
    /// <summary>The configuration section these settings bind from.</summary>
    public const string SectionName = "ObjectStore";

    /// <summary>
    /// Configuration key of the service endpoint, in the colon form. The environment-variable
    /// spelling is <c>ObjectStore__ServiceUrl</c>, which is what the startup failure names.
    /// </summary>
    public const string ServiceUrlConfigurationKey = "ObjectStore:ServiceUrl";

    /// <summary>Configuration key of the bucket name, in the colon form.</summary>
    public const string BucketConfigurationKey = "ObjectStore:Bucket";

    /// <summary>Configuration key of the access key id, in the colon form.</summary>
    public const string AccessKeyConfigurationKey = "ObjectStore:AccessKey";

    /// <summary>Configuration key of the secret access key, in the colon form.</summary>
    public const string SecretKeyConfigurationKey = "ObjectStore:SecretKey";

    /// <summary>
    /// The S3 endpoint, absolute and including its scheme — <c>http://objects:3900</c> in compose.
    /// <para>
    /// Leaving the key out entirely is what disables the store. A key that is <em>present</em> and
    /// blank is refused at startup instead: the empty string is a value an <c>.env</c> predating
    /// this section forwards by accident, and it is indistinguishable at this property from a
    /// deployment that meant to configure an endpoint and mistyped one.
    /// </para>
    /// </summary>
    public string ServiceUrl { get; set; } = string.Empty;

    /// <summary>The region the requests are signed for. Garage's own, not an AWS one.</summary>
    public string Region { get; set; } = "garage";

    /// <summary>The access key id.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>The secret access key.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>The bucket proof assets are written to.</summary>
    public string Bucket { get; set; } = string.Empty;
}
