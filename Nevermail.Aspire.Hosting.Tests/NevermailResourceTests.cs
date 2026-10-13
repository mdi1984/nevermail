using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Nevermail.Aspire.Hosting.Tests;

public sealed class NevermailResourceTests
{
    private static (IDistributedApplicationBuilder App, IResourceBuilder<NevermailResource> Mail) Create(int? httpPort = null, int? smtpPort = null)
    {
        var app = DistributedApplication.CreateBuilder();
        return (app, app.AddNevermail("mail", httpPort, smtpPort));
    }

    private static async Task<Dictionary<string, string?>> EnvironmentAsync(IResource resource)
    {
        var context = new EnvironmentCallbackContext(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in context.EnvironmentVariables)
        {
            values[key] = value switch
            {
                string s => s,
                IValueProvider provider => await provider.GetValueAsync(),
                _ => value?.ToString(),
            };
        }
        return values;
    }

    [Fact]
    public void AddNevermail_UsesTheNevermailImage()
    {
        var (_, mail) = Create();

        var image = Assert.Single(mail.Resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("docker.io", image.Registry);
        Assert.Equal("mdi1984/nevermail", image.Image);
        Assert.Equal(NevermailBuilderExtensions.DefaultImageTag, image.Tag);
        Assert.Equal("latest", NevermailBuilderExtensions.DefaultImageTag);
    }

    [Fact]
    public void AddNevermail_ExposesHttpAndSmtp()
    {
        var (_, mail) = Create(httpPort: 5110, smtpPort: 2525);

        var endpoints = mail.Resource.Annotations.OfType<EndpointAnnotation>().ToDictionary(e => e.Name);
        Assert.Equal(("http", 8080, 5110), (endpoints["http"].UriScheme, endpoints["http"].TargetPort, endpoints["http"].Port));
        Assert.Equal(("smtp", 1025, 2525), (endpoints["smtp"].UriScheme, endpoints["smtp"].TargetPort, endpoints["smtp"].Port));
        Assert.Equal("http", mail.Resource.HttpEndpoint.EndpointName);
        Assert.Equal("smtp", mail.Resource.SmtpEndpoint.EndpointName);
    }

    [Fact]
    public void AddNevermail_HasHealthCheck()
    {
        var (_, mail) = Create();

        Assert.Single(mail.Resource.Annotations.OfType<HealthCheckAnnotation>());
    }

    [Fact]
    public void ConnectionString_ContainsHttpAndSmtpEndpoints()
    {
        var (_, mail) = Create();

        var expression = mail.Resource.ConnectionStringExpression.ValueExpression;

        Assert.Equal("Endpoint={mail.bindings.http.url};SmtpHost={mail.bindings.smtp.host};SmtpPort={mail.bindings.smtp.port}", expression);
    }

    [Fact]
    public async Task WithDataVolume_MountsAndEnablesFileStorage()
    {
        var (_, mail) = Create();

        mail.WithDataVolume("nevermail-data");

        var mount = Assert.Single(mail.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal(("nevermail-data", "/app/data", ContainerMountType.Volume), (mount.Source, mount.Target, mount.Type));
        Assert.Equal("/app/data", (await EnvironmentAsync(mail.Resource))["Storage__Path"]);
    }

    [Fact]
    public async Task WithFaults_SetsFaultConfiguration()
    {
        var (_, mail) = Create();

        mail.WithFaults(rate: 0.25, statusCode: 429, delay: TimeSpan.FromMilliseconds(150));

        var env = await EnvironmentAsync(mail.Resource);
        Assert.Equal("0.25", env["Faults__Rate"]);
        Assert.Equal("429", env["Faults__StatusCode"]);
        Assert.Equal("150", env["Faults__DelayMs"]);
    }

    [Theory]
    [InlineData(-0.1, 503)]
    [InlineData(1.1, 503)]
    [InlineData(0.5, 200)]
    [InlineData(0.5, 600)]
    public void WithFaults_RejectsInvalidValues(double rate, int statusCode)
    {
        var (_, mail) = Create();

        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => mail.WithFaults(rate, statusCode));
    }

    [Fact]
    public async Task Credentials_ComeFromParameters()
    {
        var (app, mail) = Create();
        app.Configuration["Parameters:sendgrid-key"] = "sg-secret";
        app.Configuration["Parameters:smtp-user"] = "user";
        app.Configuration["Parameters:smtp-password"] = "pw";

        mail.WithSendGridApiKey(app.AddParameter("sendgrid-key", secret: true))
            .WithSmtpCredentials(app.AddParameter("smtp-user"), app.AddParameter("smtp-password", secret: true))
            .WithMaxMessages(50);

        var env = await EnvironmentAsync(mail.Resource);
        Assert.Equal("sg-secret", env["Providers__SendGrid__ApiKey"]);
        Assert.Equal("user", env["Smtp__Username"]);
        Assert.Equal("pw", env["Smtp__Password"]);
        Assert.Equal("50", env["Storage__MaxMessages"]);
    }
}
