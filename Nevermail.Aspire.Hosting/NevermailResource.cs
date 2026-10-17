namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A Nevermail container: mock HTTP APIs for SendGrid, Brevo, Mailchimp Transactional (Mandrill),
/// Azure Communication Services Email and AhaSend, an SMTP server, and a web UI showing every captured mail.
/// </summary>
/// <remarks>
/// The connection string has the form <c>Endpoint=http://host:port;SmtpHost=host;SmtpPort=port;AccessKey=...</c>.
/// <c>Endpoint</c> is the base address for all HTTP APIs (SendGrid host, Brevo <c>/v3</c>, Mandrill <c>/api/1.3/</c>, AhaSend host).
/// <c>AccessKey</c> is a dummy key, so the string also works as an Azure Communication Services connection string
/// (<c>new EmailClient(connectionString)</c>).
/// </remarks>
public sealed class NevermailResource(string name) : ContainerResource(name), IResourceWithConnectionString
{
    internal const string HttpEndpointName = "http";
    internal const string SmtpEndpointName = "smtp";

    // Base64 of "nevermail". Nevermail doesn't verify request signatures, the Azure SDK just needs a valid base64 key.
    private const string AccessKey = "bmV2ZXJtYWls";

    private EndpointReference? _httpEndpoint;
    private EndpointReference? _smtpEndpoint;

    /// <summary>HTTP endpoint: provider APIs, the inspection API and the web UI.</summary>
    public EndpointReference HttpEndpoint => _httpEndpoint ??= new(this, HttpEndpointName);

    /// <summary>SMTP endpoint (plain SMTP, no TLS).</summary>
    public EndpointReference SmtpEndpoint => _smtpEndpoint ??= new(this, SmtpEndpointName);

    /// <inheritdoc />
    public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create(
        $"Endpoint={HttpEndpoint.Property(EndpointProperty.Url)};SmtpHost={SmtpEndpoint.Property(EndpointProperty.Host)};SmtpPort={SmtpEndpoint.Property(EndpointProperty.Port)};AccessKey={AccessKey}");
}
