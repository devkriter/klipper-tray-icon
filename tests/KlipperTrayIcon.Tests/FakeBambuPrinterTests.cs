using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KlipperTrayIcon.Printers;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Xunit;

namespace KlipperTrayIcon.Tests;

/// <summary>
/// Talks to a local MQTT-over-TLS server that behaves like a Bambu printer
/// (self-signed certificate, user "bblp" + access code, report on request),
/// so the real connection code is exercised without a printer.
/// </summary>
public sealed class FakeBambuPrinterTests : IAsyncLifetime
{
    private const string Serial = "01P00A000000000";
    private const string AccessCode = "12345678";
    private MqttServer _server = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = FreePort();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=bambu-fake", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));

        var options = new MqttServerOptionsBuilder()
            .WithoutDefaultEndpoint()
            .WithEncryptedEndpoint()
            .WithEncryptedEndpointBoundIPAddress(IPAddress.Loopback)
            .WithEncryptedEndpointPort(_port)
            .WithEncryptionCertificate(cert.Export(X509ContentType.Pfx))
            .WithEncryptionSslProtocol(SslProtocols.Tls12)
            .Build();
        _server = new MqttFactory().CreateMqttServer(options);
        _server.ValidatingConnectionAsync += e =>
        {
            if (e.UserName != "bblp" || e.Password != AccessCode)
                e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            return Task.CompletedTask;
        };
        // Answer "pushall" with a full report, like the printer does.
        _server.InterceptingPublishAsync += async e =>
        {
            if (e.ApplicationMessage.Topic != $"device/{Serial}/request")
                return;
            var report = new MqttApplicationMessageBuilder()
                .WithTopic($"device/{Serial}/report")
                .WithPayload("""{"print":{"gcode_state":"RUNNING","mc_percent":37,"mc_remaining_time":12,"subtask_name":"Benchy"}}""")
                .Build();
            await _server.InjectApplicationMessage(new InjectedMqttApplicationMessage(report));
        };
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        _server.Dispose();
    }

    [Fact]
    public async Task ConnectsOverTlsAndReadsReport()
    {
        using var backend = new BambuBackend("127.0.0.1", Serial, AccessCode, _port);
        backend.Start();
        var (ok, message) = await backend.TestConnectionAsync(default);
        Assert.True(ok, message);
        Assert.Equal("Connected - printer is printing", message);

        var status = await backend.GetStatusAsync(default);
        Assert.Equal(new PrinterStatus("printing", 0.37, "Benchy", 12 * 60, "Slicer"), status);
    }

    [Fact]
    public async Task WrongAccessCodeIsExplained()
    {
        using var backend = new BambuBackend("127.0.0.1", Serial, "wrong", _port);
        backend.Start();
        var (ok, message) = await backend.TestConnectionAsync(default);
        Assert.False(ok);
        Assert.Equal("Printer rejected the access code", message);
        Assert.Equal(PrinterStatus.Offline("Printer rejected the access code"), await backend.GetStatusAsync(default));
    }

    [Fact]
    public async Task UnreachablePrinterIsExplained()
    {
        using var backend = new BambuBackend("127.0.0.1", Serial, AccessCode, FreePort());
        backend.Start();
        await Task.Delay(1500);
        Assert.Equal(PrinterStatus.Offline("Can't reach printer at 127.0.0.1"), await backend.GetStatusAsync(default));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
