using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using DotNet.Testcontainers.Configurations;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Services.Health;

using Testcontainers.PostgreSql;

namespace PostgresStudio.Integration.Tests.Server;

/// <summary>
/// S05 on real connections (W5-S2): over TCP from the test's machine without TLS it fires, and over TLS - to a server given a
/// self-signed certificate made here, with <c>CertificateRequest</c> and no package - it is quiet.
/// </summary>
/// <remarks>
/// The connection reaches the container through Docker's port mapping, so the server sees it come from the bridge's gateway,
/// never from its own loopback - which is the "another machine" S05 is about. The suite's own server has TLS off, and this
/// class's has it on.
/// </remarks>
public class ConnectionTlsLiveTests(PostgresFixture postgres, TlsServerFixture tls) : IClassFixture<TlsServerFixture>
{
    [PostgresFact]
    [HealthFires("S05")]
    public async Task S05_fires_for_a_TCP_connection_without_TLS()
    {
        string plain = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { SslMode = SslMode.Disable }.ConnectionString;

        await using PostureHost host = await PostureHost.CreateAsync(plain, environment: "Production");
        HealthCheckResult result = PostureHost.Result(await host.SweepAsync(), "S05");

        result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
        HealthFinding finding = result.Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning, "this is Production");
        finding.Remedy!.Code.Should().Be("SSL Mode=VerifyFull");
        finding.Remedy.Explanation.Should().Contain("ssl = off", "the suite's own server has TLS off, which the finding reads");
    }

    [PostgresFact]
    [HealthQuiet("S05")]
    public async Task S05_is_quiet_over_TLS()
    {
        tls.SkipIfUnavailable();

        string encrypted = new NpgsqlConnectionStringBuilder(tls.ConnectionString) { SslMode = SslMode.Require }.ConnectionString;

        await using PostureHost host = await PostureHost.CreateAsync(encrypted, environment: "Production");
        HealthCheckResult result = PostureHost.Result(await host.SweepAsync(), "S05");

        result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
        result.Findings.Should().BeEmpty();
        result.Note.Should().StartWith("The connection is encrypted (TLS");
    }
}

/// <summary>
/// A <c>postgres:17-alpine</c> of the class's own with TLS on: a self-signed certificate and key made with
/// <see cref="CertificateRequest" />, copied into the container owned by the image's <c>postgres</c> user (uid 70) with the
/// key at <c>0600</c>, as Postgres demands, and named by <c>ssl_cert_file</c> and <c>ssl_key_file</c>. Never reused.
/// </summary>
public sealed class TlsServerFixture : IAsyncLifetime
{
    /// <summary>The <c>postgres</c> user's uid and gid in the alpine images.</summary>
    private const uint Postgres = 70;

    private PostgreSqlContainer? container;
    private string? unavailable;

    /// <summary>The superuser's connection string, once the server is up.</summary>
    public string ConnectionString => container?.GetConnectionString() ?? throw new InvalidOperationException(unavailable ?? "The TLS server is not running.");

    /// <summary>Skips the test when the server could not start here.</summary>
    public void SkipIfUnavailable()
    {
        if (unavailable is { } why)
        {
            Assert.Skip(why);
        }
    }

    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            unavailable = DockerAvailability.SkipReason;
            return;
        }

        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        byte[] certificatePem = Encoding.ASCII.GetBytes(certificate.ExportCertificatePem() + "\n");
        byte[] keyPem = Encoding.ASCII.GetBytes(key.ExportPkcs8PrivateKeyPem() + "\n");

        container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithReuse(false)
            .WithResourceMapping(certificatePem, "/etc/ms-tls/server.crt", Postgres, Postgres, UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.GroupRead | UnixFileModes.OtherRead)
            .WithResourceMapping(keyPem, "/etc/ms-tls/server.key", Postgres, Postgres, UnixFileModes.UserRead | UnixFileModes.UserWrite)
            .WithCommand("-c", "ssl=on")
            .WithCommand("-c", "ssl_cert_file=/etc/ms-tls/server.crt")
            .WithCommand("-c", "ssl_key_file=/etc/ms-tls/server.key")
            .Build();

        try
        {
            await container.StartAsync();
        }
        catch (Exception exception) when (!DockerAvailability.OnContinuousIntegration)
        {
            unavailable = "The TLS server could not start here (" + exception.GetType().Name + ": " + exception.Message + ").";
            await container.DisposeAsync();
            container = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}
