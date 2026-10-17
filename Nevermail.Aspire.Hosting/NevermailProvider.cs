namespace Aspire.Hosting.ApplicationModel;

/// <summary>The provider a project talks to. Decides the shape of the Nevermail connection string.</summary>
public enum NevermailProvider
{
    /// <summary>SendGrid: <c>Endpoint=http://host:port;ApiKey=...</c>.</summary>
    SendGrid,

    /// <summary>Brevo: <c>Endpoint=http://host:port/v3;ApiKey=...</c>.</summary>
    Brevo,

    /// <summary>Mailchimp Transactional (Mandrill): <c>Endpoint=http://host:port/api/1.3/;ApiKey=...</c>.</summary>
    Mandrill,

    /// <summary>
    /// Azure Communication Services Email: <c>Endpoint=http://host:port/;AccessKey=...</c>,
    /// a regular ACS connection string for <c>new EmailClient(connectionString)</c>.
    /// </summary>
    AzureCommunicationServices,

    /// <summary>AhaSend: <c>Endpoint=http://host:port;ApiKey=...;AccountId=...</c>.</summary>
    AhaSend,

    /// <summary>SMTP: <c>Endpoint=smtp://host:port</c>, plus <c>;Username=...;Password=...</c> when credentials are required.</summary>
    Smtp,
}
