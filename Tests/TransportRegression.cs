using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using OpenCredential.AdminWeb.Services;

internal static class TransportRegression
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("Ejecutar --transport-only en Linux desechable; nunca agregar la CA de prueba al almacen Windows del anfitrion.");
        var directory = Path.Combine(Path.GetTempPath(), "oc-transport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var emptyRoots = Directory.CreateDirectory(Path.Combine(directory, "empty-roots")).FullName;
        using var root = Authority("OpenCredential ephemeral QA root");
        using var untrustedRoot = Authority("OpenCredential untrusted QA root");
        var rootPath = Path.Combine(directory, "qa-root.pem");
        await File.WriteAllTextAsync(rootPath, root.ExportCertificatePem());
        using var valid = ServerCertificate(root, false);
        using var expired = ServerCertificate(root, true);
        using var untrusted = ServerCertificate(untrustedRoot, false);
        await File.WriteAllBytesAsync(Path.Combine(directory, "valid.pfx"), valid.Export(X509ContentType.Pkcs12));
        await File.WriteAllBytesAsync(Path.Combine(directory, "expired.pfx"), expired.Export(X509ContentType.Pkcs12));
        await File.WriteAllBytesAsync(Path.Combine(directory, "untrusted.pfx"), untrusted.Export(X509ContentType.Pkcs12));
        // Root stores are cached. Set trust before a fresh process starts, not
        // after any X509 operation might already have initialized the store.
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add(typeof(TransportRegression).Assembly.Location);
        start.ArgumentList.Add("--transport-child"); start.ArgumentList.Add(directory);
        start.Environment["SSL_CERT_FILE"] = rootPath;
        start.Environment["SSL_CERT_DIR"] = emptyRoots;
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert(process.ExitCode == 0, "Fallo la prueba de transporte en el proceso aislado.");
    }

    public static async Task RunChildAsync(string directory)
    {
        using var valid = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(directory, "valid.pfx"), null);
        using var expired = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(directory, "expired.pfx"), null);
        using var untrusted = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(directory, "untrusted.pfx"), null);
        await HttpsAsync(valid, "localhost", expectedSuccess: true);
        await HttpsAsync(valid, "127.0.0.1", expectedSuccess: false);
        await HttpsAsync(expired, "localhost", expectedSuccess: false);
        await HttpsAsync(untrusted, "localhost", expectedSuccess: false);
        Console.WriteLine("PASS HTTPS: CA confiable, rechazo de nombre incorrecto, expirado y CA desconocida.");
        await SmtpAsync(valid, advertiseStartTls: true, expectedSuccess: true);
        await SmtpAsync(untrusted, advertiseStartTls: true, expectedSuccess: false);
        await SmtpAsync(valid, advertiseStartTls: false, expectedSuccess: false);
        Console.WriteLine("PASS SMTP: entrega real local con STARTTLS; sin entrega si falta TLS o la CA no es confiable.");
    }

    private static X509Certificate2 Authority(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(10));
    }

    private static X509Certificate2 ServerCertificate(X509Certificate2 issuer, bool expired)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-3),
            expired ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        return certificate.CopyWithPrivateKey(key);
    }

    private static async Task HttpsAsync(X509Certificate2 certificate, string host, bool expectedSuccess)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connected = false;
        var server = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                connected = true;
                await using var tls = new SslStream(client.GetStream(), false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
                using var request = new StreamReader(tls, Encoding.ASCII, false, 1024, leaveOpen: true);
                while (await request.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
                await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), timeout.Token);
            }
            catch (AuthenticationException) when (!expectedSuccess) { }
            catch (IOException) when (!expectedSuccess) { }
        });
        using var handler = new HttpClientHandler { UseProxy = false }; // default certificate validation remains enabled
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var success = false;
        try { success = (await http.GetStringAsync($"https://{host}:{port}/", timeout.Token)) == "ok"; }
        catch (HttpRequestException) when (!expectedSuccess) { }
        await server.WaitAsync(timeout.Token);
        Assert(connected && success == expectedSuccess, "Validacion HTTPS inesperada.");
    }

    private static async Task SmtpAsync(X509Certificate2 certificate, bool advertiseStartTls, bool expectedSuccess)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var startTlsRequested = false;
        var delivered = false;
        var payload = new StringBuilder();
        var server = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                Stream stream = client.GetStream();
                StreamReader Read(Stream value) => new(value, Encoding.ASCII, false, 1024, leaveOpen: true);
                StreamWriter Write(Stream value) => new(value, Encoding.ASCII, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
                var reader = Read(stream); var writer = Write(stream);
                await writer.WriteLineAsync("220 localhost QA SMTP");
                var encrypted = false;
                while (!timeout.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(timeout.Token);
                    if (line is null) break;
                    if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!encrypted && advertiseStartTls) await writer.WriteLineAsync("250-localhost\r\n250 STARTTLS");
                        else await writer.WriteLineAsync("250 localhost");
                    }
                    else if (line == "STARTTLS")
                    {
                        startTlsRequested = true;
                        await writer.WriteLineAsync("220 Ready for TLS");
                        reader.Dispose(); writer.Dispose();
                        var tls = new SslStream(stream, false);
                        stream = tls;
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
                        encrypted = true;
                        reader = Read(stream); writer = Write(stream);
                    }
                    else if (line.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase) || line.StartsWith("RCPT", StringComparison.OrdinalIgnoreCase))
                    {
                        Assert(encrypted, "No debe transmitir correo sin TLS.");
                        await writer.WriteLineAsync("250 OK");
                    }
                    else if (line == "DATA")
                    {
                        Assert(encrypted, "No debe transmitir tokens sin TLS.");
                        await writer.WriteLineAsync("354 End with dot");
                        while ((line = await reader.ReadLineAsync(timeout.Token)) is not null && line != ".") payload.AppendLine(line);
                        delivered = true;
                        await writer.WriteLineAsync("250 Queued");
                        break;
                    }
                    else if (line == "QUIT") { await writer.WriteLineAsync("221 Bye"); break; }
                    else throw new Exception("Comando SMTP de prueba inesperado.");
                }
                reader.Dispose(); writer.Dispose();
                await stream.DisposeAsync();
            }
            catch (AuthenticationException) when (!expectedSuccess) { }
            catch (IOException) when (!expectedSuccess) { }
        });
        var service = new RecoveryDeliveryService(Options.Create(new RecoveryDeliveryOptions
        {
            Enabled = true, Host = "localhost", Port = port, Sender = "qa@example.invalid"
        }));
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var success = false;
        try { await service.SendAsync("recipient@example.invalid", token, DateTime.UtcNow.AddMinutes(20)).WaitAsync(timeout.Token); success = true; }
        catch (SmtpException) when (!expectedSuccess) { }
        await server.WaitAsync(timeout.Token);
        Assert(success == expectedSuccess && delivered == expectedSuccess, "Entrega SMTP inesperada.");
        Assert(!advertiseStartTls || startTlsRequested, "Debe intentar negociar STARTTLS.");
        if (expectedSuccess) Assert(payload.ToString().Contains(token, StringComparison.Ordinal), "El correo debe contener el token efimero.");
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
