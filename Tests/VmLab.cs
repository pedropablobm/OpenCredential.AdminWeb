using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Npgsql;

internal static class VmLab
{
    // Explicit opt-in fixture. Never reads an existing .env or institutional DB.
    public static async Task CreateAsync(string privateDirectory, string guestInputDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Preparar este laboratorio desde el anfitrion Windows con Docker Desktop.");
        privateDirectory = Path.GetFullPath(privateDirectory);
        guestInputDirectory = Path.GetFullPath(guestInputDirectory);
        if (Directory.Exists(privateDirectory)) throw new InvalidOperationException("El directorio privado debe ser nuevo; no se sobrescriben laboratorios.");
        if (!Directory.Exists(guestInputDirectory) || File.Exists(Path.Combine(guestInputDirectory, "VmLab-settings.json")))
            throw new InvalidOperationException("Se requiere una carpeta compartida existente sin otro laboratorio preparado.");
        Directory.CreateDirectory(privateDirectory);
        var id = "oc-vmqa-" + Guid.NewGuid().ToString("N")[..10];
        var databaseContainer = id + "-db";
        var webContainer = id + "-web";
        var ownerPassword = Secret(); var clientPassword = Secret();
        var adminPassword = Secret(); var studentPassword = Secret(); var pfxPassword = Secret();
        var certificateDirectory = Directory.CreateDirectory(Path.Combine(privateDirectory, "tls")).FullName;
        using var rootKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=OpenCredential VM QA temporary CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(8));
        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback); names.AddIpAddress(IPAddress.Parse("10.0.2.2"));
        serverRequest.CertificateExtensions.Add(names.Build());
        using var unsignedKey = serverRequest.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7), RandomNumberGenerator.GetBytes(16));
        using var certificate = unsignedKey.CopyWithPrivateKey(serverKey);
        await File.WriteAllBytesAsync(Path.Combine(certificateDirectory, "server.pfx"), certificate.Export(X509ContentType.Pkcs12, pfxPassword));

        var databaseCreated = false; var webCreated = false;
        await File.WriteAllTextAsync(Path.Combine(privateDirectory, "private-state.json"), JsonSerializer.Serialize(new
        {
            Network = id, DatabaseContainer = databaseContainer, WebContainer = webContainer,
            OwnerPassword = ownerPassword, ClientPassword = clientPassword, AdminPassword = adminPassword,
            StudentPassword = studentPassword, CertificatePassword = pfxPassword
        }, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            await Docker(["network", "create", "--label", "org.opencredential.qa=vm-lab", id]);
            await Docker(["run", "-d", "--name", databaseContainer, "--label", "org.opencredential.qa=vm-lab",
                "--network", id, "--network-alias", "database", "--memory", "192m", "--cpus", "1",
                "--tmpfs", "/var/lib/postgresql/data:rw,size=128m", "-p", "127.0.0.1::5432",
                "--env", "POSTGRES_USER", "--env", "POSTGRES_PASSWORD", "--env", "POSTGRES_DB",
                "postgres:16-alpine", "-c", "shared_buffers=16MB", "-c", "max_connections=20"],
                new() { ["POSTGRES_USER"] = "ocqa_owner", ["POSTGRES_PASSWORD"] = ownerPassword, ["POSTGRES_DB"] = "ocqa_vm" });
            databaseCreated = true;
            var databasePort = await Port(databaseContainer, "5432/tcp");
            var connectionString = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1", Port = databasePort, Database = "ocqa_vm", Username = "ocqa_owner",
                Password = ownerPassword, SslMode = SslMode.Disable, Timeout = 3, CommandTimeout = 10
            };
            for (var attempt = 0; ; attempt++)
            {
                try { await using var ready = new NpgsqlConnection(connectionString.ConnectionString); await ready.OpenAsync(); break; }
                catch (NpgsqlException) when (attempt < 50) { await Task.Delay(500); }
            }
            var webConnection = new NpgsqlConnectionStringBuilder(connectionString.ConnectionString) { Host = "database", Port = 5432 };
            await Docker(["run", "-d", "--name", webContainer, "--label", "org.opencredential.qa=vm-lab",
                "--network", id, "--memory", "256m", "--cpus", "1", "--tmpfs", "/data:rw,size=32m",
                "-p", "127.0.0.1::8443", "--mount", $"type=bind,source={certificateDirectory},target=/tls,readonly",
                "--env", "ASPNETCORE_URLS", "--env", "AdminAuth__Enabled", "--env", "AdminAuth__Accounts__0__Username",
                "--env", "AdminAuth__Accounts__0__Password", "--env", "AdminAuth__Accounts__0__Role",
                "--env", "Database__Mode", "--env", "Database__Provider", "--env", "Database__ConnectionString", "--env", "Database__AutoInitialize",
                "--env", "Database__SeedDemoData", "--env", "RecoveryDelivery__Enabled",
                "--env", "Kestrel__Certificates__Default__Path", "--env", "Kestrel__Certificates__Default__Password",
                "opencredential-adminweb:review-20261019"], new()
                {
                    ["ASPNETCORE_URLS"] = "http://+:8080;https://+:8443", ["AdminAuth__Enabled"] = "true",
                    ["AdminAuth__Accounts__0__Username"] = "vmqa_admin", ["AdminAuth__Accounts__0__Password"] = adminPassword,
                    ["AdminAuth__Accounts__0__Role"] = "SuperAdmin", ["Database__Mode"] = "Sql", ["Database__Provider"] = "PostgreSql",
                    ["Database__ConnectionString"] = webConnection.ConnectionString, ["Database__AutoInitialize"] = "true",
                    ["Database__SeedDemoData"] = "false", ["RecoveryDelivery__Enabled"] = "false",
                    ["Kestrel__Certificates__Default__Path"] = "/tls/server.pfx", ["Kestrel__Certificates__Default__Password"] = pfxPassword
                });
            webCreated = true;
            var webPort = await Port(webContainer, "8443/tcp");
            using var handler = new SocketsHttpHandler { UseProxy = false, CookieContainer = new CookieContainer() };
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck }
            };
            handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(root);
            using var http = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{webPort}"), Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Add("X-OpenCredential-Request", "1");
            for (var attempt = 0; ; attempt++)
            {
                try { if ((await http.GetAsync("/health")).IsSuccessStatusCode) break; }
                catch (HttpRequestException) when (attempt < 50) { }
                if (attempt >= 50) throw new InvalidOperationException("El panel de laboratorio no inicio.");
                await Task.Delay(500);
            }
            Require(await http.PostAsJsonAsync("/api/auth/login", new { username = "vmqa_admin", password = adminPassword }), "login administrativo");
            foreach (var active in new[] { true, false })
                Require(await http.PostAsJsonAsync("/api/users", new
                {
                    username = active ? "ocqa_student" : "ocqa_inactive", firstName = "QA", lastName = "Synthetic",
                    email = active ? "student@example.invalid" : "inactive@example.invalid", documentId = active ? "QA0001" : "QA0002",
                    password = studentPassword, active, groupIds = new[] { 1 }
                }), "creacion de usuarios sinteticos");
            await using (var connection = new NpgsqlConnection(connectionString.ConnectionString))
            {
                await connection.OpenAsync();
                await using var formatted = new NpgsqlCommand("SELECT format('CREATE ROLE ocqa_client LOGIN PASSWORD %L', @password)", connection);
                formatted.Parameters.AddWithValue("password", clientPassword);
                var createRole = (string)(await formatted.ExecuteScalarAsync())!;
                await using var role = new NpgsqlCommand(createRole, connection); await role.ExecuteNonQueryAsync();
                await using var grant = new NpgsqlCommand("GRANT CONNECT ON DATABASE ocqa_vm TO ocqa_client; GRANT USAGE, CREATE ON SCHEMA public TO ocqa_client; GRANT SELECT ON ALL TABLES IN SCHEMA public TO ocqa_client; GRANT UPDATE (failed_attempts, locked_until, last_attempt_at) ON users TO ocqa_client; ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO ocqa_client", connection);
                await grant.ExecuteNonQueryAsync();
            }
            var certificateBytes = root.Export(X509ContentType.Cert);
            await File.WriteAllBytesAsync(Path.Combine(guestInputDirectory, "VmLab-root.cer"), certificateBytes);
            var settings = new
            {
                LabId = id, GuestHost = "10.0.2.2", DatabasePort = databasePort, WebPort = webPort,
                WebUrl = $"https://10.0.2.2:{webPort}", Database = "ocqa_vm", DatabaseUsername = "ocqa_client",
                ActiveUser = "ocqa_student", InactiveUser = "ocqa_inactive", AdminUsername = "vmqa_admin",
                CertificateSha256 = Convert.ToHexString(SHA256.HashData(certificateBytes)), CertificateThumbprint = root.Thumbprint,
                Note = "Synthetic lab only. PostgreSQL TLS is disabled on the loopback-only test endpoint. Never use institutional credentials."
            };
            await File.WriteAllTextAsync(Path.Combine(guestInputDirectory, "VmLab-settings.json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllTextAsync(Path.Combine(guestInputDirectory, "VmLab-access.txt"),
                $"LABORATORIO SINTETICO TEMPORAL - NO COMPARTIR ESTE ARCHIVO NI CAPTURAS DE SUS CLAVES\r\n" +
                $"Web: {settings.WebUrl}\r\nAdministrador web: vmqa_admin\r\nClave administrador web: {adminPassword}\r\n" +
                $"PostgreSQL host: 10.0.2.2\r\nPuerto: {databasePort}\r\nBase: ocqa_vm\r\nUsuario SQL: ocqa_client\r\nClave SQL: {clientPassword}\r\nTLS SQL: None (solo laboratorio en loopback)\r\n" +
                $"Usuario Windows/portal de prueba: ocqa_student\r\nUsuario inactivo: ocqa_inactive\r\nClave inicial de ambos: {studentPassword}\r\n" +
                "No cambiar la clave del administrador local de emergencia. No reconectar la VM hasta verificar la configuracion sintetica.\r\n", Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(privateDirectory, "public-endpoints.json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS VM lab: PostgreSQL/Web aislados, HTTPS validado y dos usuarios sinteticos creados. DB port={databasePort}; HTTPS port={webPort}.");
            Console.WriteLine("Las claves efimeras se entregaron en la carpeta QA de solo lectura; no se imprimen ni se guardan en Git.");
        }
        catch
        {
            if (webCreated) await StopOwned(webContainer);
            if (databaseCreated) await StopOwned(databaseContainer);
            throw new InvalidOperationException("Fallo la preparacion del laboratorio; se detuvieron sus contenedores creados, sin tocar servicios existentes. No conectar la VM.");
        }
    }

    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private static void Require(HttpResponseMessage response, string phase)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Fallo de laboratorio en {phase}: HTTP {(int)response.StatusCode}.");
        response.Dispose();
    }
    private static async Task<int> Port(string name, string port) => int.Parse((await Docker(["port", name, port])).Trim().Split(':').Last(), CultureInfo.InvariantCulture);
    private static async Task StopOwned(string name)
    {
        try { await Docker(["stop", name]); } catch { }
    }
    private static async Task<string> Docker(string[] arguments, Dictionary<string,string>? environment = null)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var output = await stdout; await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"Docker QA {arguments[0]} failed ({process.ExitCode}); sensitive environment is not printed.");
        return output;
    }
}
