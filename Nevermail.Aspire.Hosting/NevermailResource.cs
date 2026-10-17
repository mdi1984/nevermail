namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A Nevermail container: mock HTTP APIs for SendGrid, Brevo, Mailchimp Transactional (Mandrill),
/// Azure Communication Services Email and AhaSend, an SMTP server, and a web UI showing every captured mail.
/// </summary>
/// <remarks>
/// With a <see cref="Provider"/>, the connection string has the shape that provider's client needs, see <see cref="NevermailProvider"/>.
/// Without one, it has the form <c>Endpoint=http://host:port;SmtpHost=host;SmtpPort=port;AccessKey=...</c>:
/// <c>Endpoint</c> is the base address for all HTTP APIs (SendGrid host, Brevo <c>/v3</c>, Mandrill <c>/api/1.3/</c>, AhaSend host),
/// and <c>AccessKey</c> is a dummy key, so the string also works as an Azure Communication Services connection string.
/// </remarks>
public sealed class NevermailResource(string name, NevermailProvider? provider) : ContainerResource(name), IResourceWithConnectionString
{
    internal const string HttpEndpointName = "http";
    internal const string SmtpEndpointName = "smtp";

    // Base64 of "nevermail". Nevermail doesn't verify request signatures, the Azure SDK just needs a valid base64 key.
    private const string AccessKey = "bmV2ZXJtYWls";

    // Used when no key is required: Nevermail accepts any.
    private const string DefaultApiKey = "nevermail";

    // Nevermail accepts any account ID, as long as it is a UUID.
    internal const string AhaSendAccountId = "00000000-0000-0000-0000-000000000000";

    private EndpointReference? _httpEndpoint;
    private EndpointReference? _smtpEndpoint;

    /// <summary>Creates a resource whose connection string covers every provider.</summary>
    public NevermailResource(string name) : this(name, null)
    {
    }

    /// <summary>The provider the connection string is shaped for, or <see langword="null"/> for the combined form.</summary>
    public NevermailProvider? Provider { get; } = provider;

    /// <summary>HTTP endpoint: provider APIs, the inspection API and the web UI.</summary>
    public EndpointReference HttpEndpoint => _httpEndpoint ??= new(this, HttpEndpointName);

    /// <summary>SMTP endpoint (plain SMTP, no TLS).</summary>
    public EndpointReference SmtpEndpoint => _smtpEndpoint ??= new(this, SmtpEndpointName);

    // Set by With…ApiKey for the matching provider and WithSmtpCredentials, so the connection string carries them.
    internal ParameterResource? ApiKey { get; set; }
    internal ParameterResource? SmtpUserName { get; set; }
    internal ParameterResource? SmtpPassword { get; set; }

    /// <inheritdoc />
    public ReferenceExpression ConnectionStringExpression
    {
        get
        {
            var url = HttpEndpoint.Property(EndpointProperty.Url);
            var builder = new ReferenceExpressionBuilder();
            switch (Provider)
            {
                case null:
                    builder.Append($"Endpoint={url};SmtpHost={SmtpEndpoint.Property(EndpointProperty.Host)};SmtpPort={SmtpEndpoint.Property(EndpointProperty.Port)};AccessKey={AccessKey}");
                    break;
                case NevermailProvider.SendGrid:
                    builder.Append($"Endpoint={url}");
                    AppendApiKey(builder);
                    break;
                case NevermailProvider.Brevo:
                    builder.Append($"Endpoint={url}/v3");
                    AppendApiKey(builder);
                    break;
                case NevermailProvider.Mandrill:
                    builder.Append($"Endpoint={url}/api/1.3/");
                    AppendApiKey(builder);
                    break;
                case NevermailProvider.AzureCommunicationServices:
                    builder.Append($"endpoint={url}/;accesskey={AccessKey}");
                    break;
                case NevermailProvider.AhaSend:
                    builder.Append($"Endpoint={url}");
                    AppendApiKey(builder);
                    builder.Append($";AccountId={AhaSendAccountId}");
                    break;
                case NevermailProvider.Smtp:
                    builder.Append($"Endpoint=smtp://{SmtpEndpoint.Property(EndpointProperty.HostAndPort)}");
                    if (SmtpUserName is not null && SmtpPassword is not null)
                    {
                        builder.Append($";Username={SmtpUserName};Password={SmtpPassword}");
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unknown provider {Provider}.");
            }
            return builder.Build();
        }
    }

    private void AppendApiKey(ReferenceExpressionBuilder builder)
    {
        if (ApiKey is not null)
        {
            builder.Append($";ApiKey={ApiKey}");
        }
        else
        {
            builder.AppendLiteral($";ApiKey={DefaultApiKey}");
        }
    }
}
