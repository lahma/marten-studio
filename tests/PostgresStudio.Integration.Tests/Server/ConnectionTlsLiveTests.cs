using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
/// S11 (1.1-NP2) on a real TLS server: the connection is encrypted and the data source does not check the certificate, so it
/// fires at <c>SSL Mode=Require</c>; at <c>VerifyCA</c> with the server's own certificate as the root it is quiet; and a loopback
/// host never leaves the machine, so it is quiet whatever the mode. The data source must name a host that is not loopback for
/// the check to judge it, so the container's published port is reached through this machine's own address - the test skips,
/// saying so, where Docker publishes it on loopback only, and fails there on continuous integration (D19).
/// </summary>
public class ServerCertificateLiveTests(TlsServerFixture tls) : IClassFixture<TlsServerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    [HealthFires("S11")]
    public async Task S11_fires_for_an_encrypted_connection_that_does_not_check_the_certificate()
    {
        tls.SkipIfUnavailable();
        string connectionString = await RemoteAsync(tls, SslMode.Require);

        await using PostureHost host = await PostureHost.CreateAsync(connectionString, environment: "Production");
        HealthCheckResult result = PostureHost.Result(await host.SweepAsync(), "S11");

        result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
        HealthFinding finding = result.Findings.Should().ContainSingle().Subject;
        finding.Variant.Should().Be("require");
        finding.Severity.Should().Be(HealthSeverity.Info, "no managed platform marks this server");
        finding.Evidence.Should().Contain(x => x.Label == "Connection" && x.Value.StartsWith("encrypted (TLS", StringComparison.Ordinal));
        finding.Remedy!.Code.Should().Contain("SSL Mode=VerifyFull;Root Certificate=");
        finding.Remedy.Code.Should().Contain("b.ConnectionStringBuilder.SslMode = SslMode.VerifyFull;");
    }

    [PostgresFact]
    [HealthQuiet("S11")]
    public async Task S11_is_quiet_when_the_certificate_is_checked_and_when_the_host_is_loopback()
    {
        tls.SkipIfUnavailable();

        string root = Path.Combine(Path.GetTempPath(), "ms-s11-" + Guid.NewGuid().ToString("N") + ".pem");
        await File.WriteAllTextAsync(root, tls.CertificatePem, Token);

        try
        {
            string verified = await RemoteAsync(tls, SslMode.VerifyCA, root);

            await using (PostureHost host = await PostureHost.CreateAsync(verified, environment: "Production"))
            {
                HealthCheckResult result = PostureHost.Result(await host.SweepAsync(), "S11");

                result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
                result.Findings.Should().BeEmpty();
                result.Note.Should().Contain("checks the server's certificate");
            }

            string loopback = new NpgsqlConnectionStringBuilder(tls.ConnectionString) { SslMode = SslMode.Require }.ConnectionString;

            await using (PostureHost host = await PostureHost.CreateAsync(loopback, environment: "Production"))
            {
                HealthCheckResult result = PostureHost.Result(await host.SweepAsync(), "S11");

                result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
                result.Findings.Should().BeEmpty();
                result.Note.Should().Contain("never leaves it");
            }
        }
        finally
        {
            File.Delete(root);
        }
    }

    /// <summary>The TLS server's connection string with its host replaced by this machine's own address, or a skip.</summary>
    private static async Task<string> RemoteAsync(TlsServerFixture tls, SslMode mode, string? rootCertificate = null)
    {
        NpgsqlConnectionStringBuilder builder = new(tls.ConnectionString) { SslMode = mode, Pooling = true };

        IPAddress? address = NetworkInterface.GetAllNetworkInterfaces()
            .Where(static x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(static x => x.GetIPProperties().UnicastAddresses)
            .Select(static x => x.Address)
            .FirstOrDefault(static x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x) && !x.ToString().StartsWith("169.254.", StringComparison.Ordinal));

        if (address is null)
        {
            SkipLocallyOrFail("This machine has no non-loopback IPv4 address, so S11 cannot be shown a remote host.");
        }

        builder.Host = address!.ToString();

        if (rootCertificate is not null)
        {
            builder.RootCertificate = rootCertificate;
        }

        try
        {
            await using NpgsqlConnection probe = new(builder.ConnectionString);
            await probe.OpenAsync(Token);
        }
        catch (Exception exception) when (exception is NpgsqlException or SocketException or IOException)
        {
            SkipLocallyOrFail("The TLS container's published port is not reachable on this machine's own address (" + exception.GetType().Name + "), so S11 cannot be shown a remote host.");
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// D19's rule for a precondition of the machine: locally the test skips, saying why; on continuous integration (<c>CI</c> or
    /// <c>GITHUB_ACTIONS</c> set) it fails with the same reason, so no run is green without having shown S11 a remote host.
    /// </summary>
    /// <param name="reason">What is missing.</param>
    private static void SkipLocallyOrFail(string reason)
    {
        bool continuousIntegration = DockerAvailability.OnContinuousIntegration
                                     || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));

        if (continuousIntegration)
        {
            throw new InvalidOperationException(reason + " This run is on continuous integration, where a skipped live test reported as a pass is worse than a failure.");
        }

        Assert.Skip(reason);
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

    /// <summary>The server's self-signed certificate, PEM: the root a <c>VerifyCA</c> data source trusts.</summary>
    public string CertificatePem { get; private set; } = string.Empty;

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

        CertificatePem = certificate.ExportCertificatePem() + "\n";
        byte[] certificatePem = Encoding.ASCII.GetBytes(CertificatePem);
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
