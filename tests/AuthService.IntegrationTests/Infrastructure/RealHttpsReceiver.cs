using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>
/// A certificate authority that exists only for a test run, and the server certificates it signs. A client that trusts only this
/// authority checks the real things a client checks (the chain, the dates, the name); only who is trusted differs from production.
/// </summary>
public sealed class TestAuthority : IDisposable
{
    private readonly X509Certificate2 _root;

    public TestAuthority(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        // Re-imported so the private half is usable for signing on every platform.
        _root = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    /// <summary>A server certificate for <c>localhost</c> and 127.0.0.1, valid for <paramref name="validFor"/> from a day ago.</summary>
    public X509Certificate2 IssueServerCertificate(TimeSpan? validFor = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        using var signed = request.Create(_root, notBefore, notBefore + (validFor ?? TimeSpan.FromDays(7)), RandomNumberGenerator.GetBytes(16));
        using var withKey = signed.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    /// <summary>
    /// What an HTTP client does when it trusts only this authority: the certificate must chain to it, be in date and name the host. The
    /// system's own trust store is not consulted, so a certificate from any other authority is refused.
    /// </summary>
    public bool Trusts(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(new X509Certificate2(certificate));
    }

    public void Dispose() => _root.Dispose();
}

/// <summary>
/// A product's server for the webhook tests that need the real thing: Kestrel listening on a real socket with a real TLS certificate,
/// reached through a real DNS lookup of <c>localhost</c>. It records every request to <c>/hooks</c> and answers as the test says.
/// <c>/moved</c> counts the visits a followed redirect would make.
/// </summary>
public sealed class RealHttpsReceiver : IAsyncDisposable
{
    public sealed record Received(string Path, string Body, IReadOnlyDictionary<string, string> Headers, DateTimeOffset At);

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Received> _received = new();
    private int _movedVisits;

    private RealHttpsReceiver(WebApplication app, TimeProvider clock)
    {
        _app = app;

        app.MapMethods("/hooks", ["POST"], async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            var headers = context.Request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            _received.Enqueue(new Received(context.Request.Path, body, headers, clock.GetUtcNow()));

            await Respond(context);
        });
        app.Map("/moved", () =>
        {
            Interlocked.Increment(ref _movedVisits);
            return Results.Ok();
        });
    }

    public int Port { get; private set; }

    /// <summary>The address to register as the callback.</summary>
    public string HooksUrl => $"https://localhost:{Port}/hooks";

    public IReadOnlyList<Received> Requests => [.. _received];

    public int MovedVisits => Volatile.Read(ref _movedVisits);

    /// <summary>How the "product" answers a webhook. Replace it to fail, redirect or stall.</summary>
    public Func<HttpContext, Task> Respond { get; set; } = context =>
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        return Task.CompletedTask;
    };

    public static async Task<RealHttpsReceiver> StartAsync(X509Certificate2 certificate, TimeProvider clock)
    {
        // "localhost" can resolve to ::1 first. Listening on both loopback addresses, on one port, means no connection waits on a
        // refused one (on Windows a refused loopback connection takes about two seconds to fail).
        var port = FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate));
            if (Socket.OSSupportsIPv6)
            {
                kestrel.Listen(IPAddress.IPv6Loopback, port, listen => listen.UseHttps(certificate));
            }
        });

        var app = builder.Build();
        var receiver = new RealHttpsReceiver(app, clock) { Port = port };
        await app.StartAsync();
        return receiver;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
