namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A Nevermail container: mock HTTP APIs for SendGrid, Brevo, Mailchimp Transactional (Mandrill),
/// Azure Communication Services Email and AhaSend, an SMTP server, and a web UI showing every captured mail.
/// </summary>
/// <remarks>
/// With a <see cref="Provider"/>, the connection string has the shape that provider's client needs, see <see cref="NevermailProvider"/>,
/// and <see cref="GetConnectionProperties"/> has its values one by one.
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
            // The connection string is the properties joined as Key=Value, except that SMTP's Host and Port go in as one smtp:// Endpoint.
            var builder = new ReferenceExpressionBuilder();
            foreach (var (key, value) in GetConnectionProperties())
            {
                if (Provider == NevermailProvider.Smtp && key is "Host" or "Port")
                {
                    continue;
                }
                if (!builder.IsEmpty)
                {
                    builder.AppendLiteral(";");
                }
                builder.AppendLiteral($"{key}=");
                builder.AppendFormatted(value);
            }
            return builder.Build();
        }
    }

    /// <summary>
    /// The values in the connection string, one by one, so the app host can pass them to a project's own settings:
    /// <c>WithEnvironment("SendGrid__ApiKey", mail.Resource.GetConnectionProperty("ApiKey"))</c>.
    /// <c>WithReference</c> also sets each one as <c>{NAME}_{KEY}</c>, e.g. <c>MAIL_APIKEY</c>.
    /// </summary>
    /// <remarks>
    /// <c>Endpoint</c> and <c>ApiKey</c> for SendGrid, Brevo and Mandrill, plus <c>AccountId</c> for AhaSend.
    /// <c>Endpoint</c> and <c>AccessKey</c> for Azure Communication Services.
    /// <c>Endpoint</c> (<c>smtp://host:port</c>), <c>Host</c> and <c>Port</c> for SMTP, plus <c>Username</c> and <c>Password</c> when credentials are required.
    /// <c>Endpoint</c>, <c>SmtpHost</c>, <c>SmtpPort</c> and <c>AccessKey</c> without a provider.
    /// </remarks>
    public IEnumerable<KeyValuePair<string, ReferenceExpression>> GetConnectionProperties()
    {
        var url = HttpEndpoint.Property(EndpointProperty.Url);
        switch (Provider)
        {
            case null:
                yield return new("Endpoint", ReferenceExpression.Create($"{url}"));
                yield return new("SmtpHost", ReferenceExpression.Create($"{SmtpEndpoint.Property(EndpointProperty.Host)}"));
                yield return new("SmtpPort", ReferenceExpression.Create($"{SmtpEndpoint.Property(EndpointProperty.Port)}"));
                yield return new("AccessKey", ReferenceExpression.Create($"{AccessKey}"));
                break;
            case NevermailProvider.SendGrid:
                yield return new("Endpoint", ReferenceExpression.Create($"{url}"));
                yield return new("ApiKey", ApiKeyExpression);
                break;
            case NevermailProvider.Brevo:
                yield return new("Endpoint", ReferenceExpression.Create($"{url}/v3"));
                yield return new("ApiKey", ApiKeyExpression);
                break;
            case NevermailProvider.Mandrill:
                yield return new("Endpoint", ReferenceExpression.Create($"{url}/api/1.3/"));
                yield return new("ApiKey", ApiKeyExpression);
                break;
            case NevermailProvider.AzureCommunicationServices:
                yield return new("Endpoint", ReferenceExpression.Create($"{url}/"));
                yield return new("AccessKey", ReferenceExpression.Create($"{AccessKey}"));
                break;
            case NevermailProvider.AhaSend:
                yield return new("Endpoint", ReferenceExpression.Create($"{url}"));
                yield return new("ApiKey", ApiKeyExpression);
                yield return new("AccountId", ReferenceExpression.Create($"{AhaSendAccountId}"));
                break;
            case NevermailProvider.Smtp:
                yield return new("Endpoint", ReferenceExpression.Create($"smtp://{SmtpEndpoint.Property(EndpointProperty.HostAndPort)}"));
                yield return new("Host", ReferenceExpression.Create($"{SmtpEndpoint.Property(EndpointProperty.Host)}"));
                yield return new("Port", ReferenceExpression.Create($"{SmtpEndpoint.Property(EndpointProperty.Port)}"));
                if (SmtpUserName is not null && SmtpPassword is not null)
                {
                    yield return new("Username", ReferenceExpression.Create($"{SmtpUserName}"));
                    yield return new("Password", ReferenceExpression.Create($"{SmtpPassword}"));
                }
                break;
            default:
                throw new InvalidOperationException($"Unknown provider {Provider}.");
        }
    }

    private ReferenceExpression ApiKeyExpression =>
        ApiKey is not null ? ReferenceExpression.Create($"{ApiKey}") : ReferenceExpression.Create($"{DefaultApiKey}");
}
