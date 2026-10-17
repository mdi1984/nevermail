using System.Globalization;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>Adds <see cref="NevermailResource"/> to an Aspire app host.</summary>
public static class NevermailBuilderExtensions
{
    internal const string Registry = "docker.io";
    internal const string Image = "mdi1984/nevermail";
    internal const int HttpTargetPort = 8080;
    internal const int SmtpTargetPort = 1025;
    internal const string DataPath = "/app/data";

    /// <summary>The image tag this package was built for (e.g. <c>v1.2.0</c>), or <c>latest</c> for local builds.</summary>
    public static string DefaultImageTag { get; } =
        typeof(NevermailBuilderExtensions).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "NevermailImageTag")?.Value is { Length: > 0 } tag ? tag : "latest";

    /// <summary>
    /// Adds a Nevermail container. Reference it from a project with <c>WithReference</c> to get a connection string
    /// that covers every provider: <c>Endpoint=...;SmtpHost=...;SmtpPort=...;AccessKey=...</c>.
    /// Use the overload with a <see cref="NevermailProvider"/> to get the shape one provider's client needs.
    /// </summary>
    /// <param name="builder">The app host builder.</param>
    /// <param name="name">Resource name.</param>
    /// <param name="httpPort">Host port for the HTTP APIs and web UI. Random if not set.</param>
    /// <param name="smtpPort">Host port for SMTP. Random if not set.</param>
    public static IResourceBuilder<NevermailResource> AddNevermail(this IDistributedApplicationBuilder builder, [ResourceName] string name, int? httpPort = null, int? smtpPort = null) =>
        AddNevermail(builder, new NevermailResource(name), httpPort, smtpPort);

    /// <summary>
    /// Adds a Nevermail container whose connection string has the shape <paramref name="provider"/>'s client needs,
    /// e.g. <c>Endpoint=http://localhost:54321;ApiKey=...</c> for SendGrid. See <see cref="NevermailProvider"/> for every shape.
    /// </summary>
    /// <param name="builder">The app host builder.</param>
    /// <param name="name">Resource name.</param>
    /// <param name="provider">The provider your project talks to.</param>
    /// <param name="httpPort">Host port for the HTTP APIs and web UI. Random if not set.</param>
    /// <param name="smtpPort">Host port for SMTP. Random if not set.</param>
    public static IResourceBuilder<NevermailResource> AddNevermail(this IDistributedApplicationBuilder builder, [ResourceName] string name, NevermailProvider provider, int? httpPort = null, int? smtpPort = null)
    {
        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider.");
        }
        return AddNevermail(builder, new NevermailResource(name, provider), httpPort, smtpPort);
    }

    private static IResourceBuilder<NevermailResource> AddNevermail(IDistributedApplicationBuilder builder, NevermailResource resource, int? httpPort, int? smtpPort)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddResource(resource)
            .WithImage(Image, DefaultImageTag)
            .WithImageRegistry(Registry)
            .WithHttpEndpoint(port: httpPort, targetPort: HttpTargetPort, name: NevermailResource.HttpEndpointName)
            .WithEndpoint(port: smtpPort, targetPort: SmtpTargetPort, name: NevermailResource.SmtpEndpointName, scheme: "smtp")
            .WithHttpHealthCheck("/health", endpointName: NevermailResource.HttpEndpointName)
            .WithUrlForEndpoint(NevermailResource.HttpEndpointName, url => url.DisplayText = "Inbox")
            .WithUrlForEndpoint(NevermailResource.SmtpEndpointName, url => url.DisplayLocation = UrlDisplayLocation.DetailsOnly);
    }

    /// <summary>Keeps captured mails in a named volume, so they survive restarts of the app host.</summary>
    public static IResourceBuilder<NevermailResource> WithDataVolume(this IResourceBuilder<NevermailResource> builder, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), DataPath)
            .WithEnvironment("Storage__Path", DataPath);
    }

    /// <summary>Keeps captured mails in a directory on the host.</summary>
    public static IResourceBuilder<NevermailResource> WithDataBindMount(this IResourceBuilder<NevermailResource> builder, string source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);
        return builder
            .WithBindMount(source, DataPath)
            .WithEnvironment("Storage__Path", DataPath);
    }

    /// <summary>Requires this SendGrid API key. Without it, any key is accepted. Also goes into a SendGrid connection string.</summary>
    public static IResourceBuilder<NevermailResource> WithSendGridApiKey(this IResourceBuilder<NevermailResource> builder, IResourceBuilder<ParameterResource> apiKey) =>
        WithApiKey(builder, NevermailProvider.SendGrid, "Providers__SendGrid__ApiKey", apiKey);

    /// <summary>Requires this Brevo API key. Without it, any key is accepted. Also goes into a Brevo connection string.</summary>
    public static IResourceBuilder<NevermailResource> WithBrevoApiKey(this IResourceBuilder<NevermailResource> builder, IResourceBuilder<ParameterResource> apiKey) =>
        WithApiKey(builder, NevermailProvider.Brevo, "Providers__Brevo__ApiKey", apiKey);

    /// <summary>Requires this Mailchimp Transactional (Mandrill) API key. Without it, any key is accepted. Also goes into a Mailchimp Transactional (Mandrill) connection string.</summary>
    public static IResourceBuilder<NevermailResource> WithMandrillApiKey(this IResourceBuilder<NevermailResource> builder, IResourceBuilder<ParameterResource> apiKey) =>
        WithApiKey(builder, NevermailProvider.Mandrill, "Providers__Mandrill__ApiKey", apiKey);

    /// <summary>Requires this AhaSend API key (<c>Authorization: Bearer</c>). Without it, any key is accepted. Also goes into an AhaSend connection string.</summary>
    public static IResourceBuilder<NevermailResource> WithAhaSendApiKey(this IResourceBuilder<NevermailResource> builder, IResourceBuilder<ParameterResource> apiKey) =>
        WithApiKey(builder, NevermailProvider.AhaSend, "Providers__AhaSend__ApiKey", apiKey);

    /// <summary>Requires SMTP clients to authenticate with these credentials. Without them, any (or no) credentials are accepted.</summary>
    public static IResourceBuilder<NevermailResource> WithSmtpCredentials(this IResourceBuilder<NevermailResource> builder, IResourceBuilder<ParameterResource> userName, IResourceBuilder<ParameterResource> password)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(password);
        builder.Resource.SmtpUserName = userName.Resource;
        builder.Resource.SmtpPassword = password.Resource;
        return builder.WithEnvironment("Smtp__Username", userName).WithEnvironment("Smtp__Password", password);
    }

    /// <summary>
    /// Makes a share of all sends fail on purpose, to exercise retries and error handling.
    /// Single sends can also be failed by sending to <c>someone@fail-&lt;code&gt;.test</c>.
    /// </summary>
    /// <param name="builder">The Nevermail resource.</param>
    /// <param name="rate">Share of sends that fail, 0 to 1.</param>
    /// <param name="statusCode">HTTP status for failed API sends (SMTP answers 451).</param>
    /// <param name="delay">Delay added to every send, like a slow provider.</param>
    public static IResourceBuilder<NevermailResource> WithFaults(this IResourceBuilder<NevermailResource> builder, double rate = 0, int statusCode = 503, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(rate, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(statusCode, 400);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(statusCode, 599);

        return builder
            .WithEnvironment("Faults__Rate", rate.ToString(CultureInfo.InvariantCulture))
            .WithEnvironment("Faults__StatusCode", statusCode.ToString(CultureInfo.InvariantCulture))
            .WithEnvironment("Faults__DelayMs", ((int)(delay ?? TimeSpan.Zero).TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Caps how many mails are kept; the oldest are dropped first.</summary>
    public static IResourceBuilder<NevermailResource> WithMaxMessages(this IResourceBuilder<NevermailResource> builder, int maxMessages)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegative(maxMessages);
        return builder.WithEnvironment("Storage__MaxMessages", maxMessages.ToString(CultureInfo.InvariantCulture));
    }

    // Requires the key in Nevermail and, when the resource is shaped for this provider, puts it into the connection string.
    private static IResourceBuilder<NevermailResource> WithApiKey(IResourceBuilder<NevermailResource> builder, NevermailProvider provider, string variable, IResourceBuilder<ParameterResource> apiKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(apiKey);
        if (builder.Resource.Provider == provider)
        {
            builder.Resource.ApiKey = apiKey.Resource;
        }
        return builder.WithEnvironment(variable, apiKey);
    }
}
