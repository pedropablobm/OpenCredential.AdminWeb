using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using OpenCredential.AdminWeb.Services;

internal static class HttpRegression
{
    public static async Task RunAsync(string root, bool databaseDown = false, bool trustedProxy = false)
    {
        var directory = Path.Combine(root, trustedProxy ? "http-trusted-proxy" : databaseDown ? "http-db-down" : "http");
        Directory.CreateDirectory(directory);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(AdminAuthService).Assembly.Location);
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add($"http://127.0.0.1:{port}");
        start.ArgumentList.Add("--contentRoot"); start.ArgumentList.Add(directory);
        start.Environment["ADMINWEB_DATA_DIR"] = directory;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["Database__Mode"] = databaseDown ? "Sql" : "Json";
        if (databaseDown)
        {
            start.Environment["Database__Provider"] = "PostgreSql";
            start.Environment["Database__ConnectionString"] = "Host=127.0.0.1;Port=1;Username=test;Password=synthetic-http-secret;Database=unavailable;SSL Mode=VerifyFull;Root Certificate=/synthetic/ca.crt;Timeout=1;CommandTimeout=1";
        }
        start.Environment["AdminAuth__Enabled"] = "true";
        if (trustedProxy) start.Environment["ForwardedHeaders__TrustedProxies__0"] = "127.0.0.1";
        var password = "Ephemeral test " + Guid.NewGuid().ToString("N");
        var roles = new[] { "SuperAdmin", "Coordinator", "Operator" };
        for (var index = 0; index < roles.Length; index++)
        {
            start.Environment[$"AdminAuth__Accounts__{index}__Username"] = roles[index];
            start.Environment[$"AdminAuth__Accounts__{index}__Password"] = password;
            start.Environment[$"AdminAuth__Accounts__{index}__Role"] = roles[index];
        }
        using var server = Process.Start(start)!;
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();
        try
        {
            HttpClient Client() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            using var admin = Client();
            for (var attempt = 0; ; attempt++)
            {
                try { if ((await admin.GetAsync("/health")).IsSuccessStatusCode) break; }
                catch (HttpRequestException) { }
                if (attempt > 100 || server.HasExited) throw new Exception("Servidor de prueba no inicio.");
                await Task.Delay(100);
            }
            using var proxyLogin = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new { username = "SuperAdmin", password })
            };
            proxyLogin.Headers.Host = "admin.example.invalid";
            proxyLogin.Headers.Add("X-Forwarded-Proto", "https");
            proxyLogin.Headers.Add("X-Forwarded-For", "203.0.113.10");
            proxyLogin.Headers.Add("X-OpenCredential-Request", "1");
            using var proxyResponse = await admin.SendAsync(proxyLogin);
            if (trustedProxy)
            {
                Assert(proxyResponse.IsSuccessStatusCode, "Proxy confiable debe conservar HTTPS sin bucle de redireccion.");
                var cookie = proxyResponse.Headers.GetValues("Set-Cookie").First();
                Assert(cookie.Contains("secure", StringComparison.OrdinalIgnoreCase), "La cookie detras del proxy HTTPS debe ser Secure.");
                using var read = new HttpRequestMessage(HttpMethod.Get, "/api/summary");
                read.Headers.Host = "admin.example.invalid";
                read.Headers.Add("X-Forwarded-Proto", "https");
                read.Headers.Add("X-Forwarded-For", "203.0.113.10");
                read.Headers.Add("Cookie", cookie.Split(';')[0]); // only an ephemeral test cookie; never print it
                using var response = await admin.SendAsync(read);
                Assert(response.IsSuccessStatusCode, "La sesion debe funcionar a traves del proxy confiable.");
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert(body.GetProperty("auditEntries").EnumerateArray().Any(entry => entry.GetProperty("remoteIp").GetString() == "203.0.113.10"), "La auditoria debe registrar la IP reenviada por el proxy confiable.");
                return;
            }
            Assert(proxyResponse.StatusCode == HttpStatusCode.PermanentRedirect, "Encabezados de proxy no configurado no deben saltarse HTTPS.");
            Assert((await admin.GetAsync("/api/summary")).StatusCode == HttpStatusCode.Unauthorized, "API anonima debe negar acceso.");
            Assert((await admin.GetAsync("/api/users")).StatusCode == HttpStatusCode.Unauthorized, "Listado paginado anonimo debe negar acceso.");
            Assert((await admin.PostAsJsonAsync("/api/auth/login", new { username = "SuperAdmin", password })).StatusCode == HttpStatusCode.Forbidden, "POST sin cabecera CSRF debe rechazarse.");
            admin.DefaultRequestHeaders.Add("X-OpenCredential-Request", "1");
            Assert((await admin.PostAsJsonAsync("/api/auth/login", new { username = "SuperAdmin", password })).IsSuccessStatusCode, "Login admin.");
            if (databaseDown)
            {
                var config = await admin.GetFromJsonAsync<JsonElement>("/api/configuration/database");
                Assert(config.GetProperty("connectionDetailsAvailable").GetBoolean() && config.GetProperty("sslMode").GetString() == "VerifyFull", "Bootstrap muestra TLS incluso con la base caida.");
                Assert(config.GetProperty("rootCertificateConfigured").GetBoolean() && !config.GetProperty("passwordConfigured").GetBoolean(), "El certificado se indica sin reutilizar la clave del despliegue.");
                Assert(config.GetProperty("rootCertificate").GetString() == "/synthetic/ca.crt", "La ruta publica de la CA se conserva para la interfaz autorizada.");
                Assert(!config.ToString().Contains("synthetic-http-secret", StringComparison.Ordinal), "La respuesta no expone la clave de la base.");
                using var readOnly = Client();
                readOnly.DefaultRequestHeaders.Add("X-OpenCredential-Request", "1");
                Assert((await readOnly.PostAsJsonAsync("/api/auth/login", new { username = "Operator", password })).IsSuccessStatusCode, "Operador inicia sesion con cuenta configurada.");
                Assert((await readOnly.GetAsync("/api/configuration/database")).StatusCode == HttpStatusCode.Forbidden, "Los detalles de conexion siguen restringidos a SuperAdmin.");
                Assert((await admin.GetAsync("/health/ready")).StatusCode == HttpStatusCode.ServiceUnavailable, "Readiness detecta base caida.");
                return;
            }
            var pdf = await admin.GetAsync("/api/reports/pdf");
            Assert(pdf.IsSuccessStatusCode && pdf.Content.Headers.ContentType?.MediaType == "application/pdf", "Exportacion PDF operativa.");
            var created = await admin.PostAsJsonAsync("/api/users", new { username = "http-student", firstName = "Test", lastName = "User", email = "test@example.invalid", documentId = "123", active = true, password, groupIds = new[] { 1 } });
            Assert(created.IsSuccessStatusCode, "Crear usuario HTTP.");
            var userId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
            var page = await admin.GetFromJsonAsync<JsonElement>("/api/users?page=1&pageSize=1&search=http-student");
            Assert(page.GetProperty("items").GetArrayLength() == 1 && page.GetProperty("total").GetInt32() == 1, "Listado paginado HTTP.");
            Assert(!page.ToString().Contains("passwordHash", StringComparison.OrdinalIgnoreCase), "Listado no expone hashes.");
            Assert((await admin.GetAsync("/api/users?pageSize=101")).StatusCode == HttpStatusCode.BadRequest, "Tamano de pagina limitado.");
            var compact = await admin.GetFromJsonAsync<JsonElement>("/api/summary?compact=true");
            Assert(compact.GetProperty("users").GetArrayLength() == 0 && compact.GetProperty("usageRecords").GetArrayLength() == 0, "Resumen compacto mantiene usuarios separados.");
            var privileged = await admin.PostAsJsonAsync("/api/users", new { username = "http-privileged", firstName = "Test", lastName = "Admin", email = "test@example.invalid", documentId = "124", active = true, password, groupIds = new[] { 7 } });
            Assert(privileged.IsSuccessStatusCode, "Crear usuario privilegiado HTTP.");
            var privilegedId = (await privileged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
            using var coordinator = Client(); coordinator.DefaultRequestHeaders.Add("X-OpenCredential-Request", "1");
            Assert((await coordinator.PostAsJsonAsync("/api/auth/login", new { username = "Coordinator", password })).IsSuccessStatusCode, "Login coordinador.");
            Assert((await coordinator.GetAsync("/api/users")).IsSuccessStatusCode, "Coordinador puede consultar listado paginado.");
            Assert((await coordinator.PostAsJsonAsync($"/api/users/{privilegedId}/password", new { generate = true })).StatusCode == HttpStatusCode.Forbidden, "Coordinador no puede resetear administrador.");
            Assert((await coordinator.PutAsJsonAsync($"/api/users/{userId}", new { username = "http-student", firstName = "Test", lastName = "User", email = "test@example.invalid", documentId = "123", active = true, groupIds = new[] { 7 } })).StatusCode == HttpStatusCode.Forbidden, "Coordinador no puede asignar SuperAdmin.");
            var summary = await coordinator.GetFromJsonAsync<JsonElement>("/api/summary");
            Assert(summary.GetProperty("auditEntries").GetArrayLength() == 0, "Coordinador no debe recibir auditoria.");
            using var op = Client(); op.DefaultRequestHeaders.Add("X-OpenCredential-Request", "1");
            Assert((await op.PostAsJsonAsync("/api/auth/login", new { username = "Operator", password })).IsSuccessStatusCode, "Login operador.");
            Assert((await op.GetAsync("/api/users")).StatusCode == HttpStatusCode.Forbidden, "Operador no debe recibir listado paginado de usuarios.");
            summary = await op.GetFromJsonAsync<JsonElement>("/api/summary");
            Assert(summary.GetProperty("users").GetArrayLength() == 0, "Operador no debe recibir datos personales.");
            using var portal = Client(); portal.DefaultRequestHeaders.Add("X-OpenCredential-Request", "1");
            Assert((await portal.PostAsJsonAsync("/api/portal/auth/login", new { username = "http-student", password })).IsSuccessStatusCode, "Login portal.");
            Assert((await portal.GetAsync("/api/portal/me")).IsSuccessStatusCode, "Perfil portal autorizado.");
            Assert((await admin.PostAsJsonAsync($"/api/users/{userId}/password", new { generate = true })).IsSuccessStatusCode, "Reset admin.");
            Assert((await portal.GetAsync("/api/portal/me")).StatusCode == HttpStatusCode.Unauthorized, "Reset debe revocar cookie previa.");
            Assert((await admin.PostAsJsonAsync("/api/portal/auth/recover", new { username = "http-student", email = "test@example.invalid", documentId = "123" })).StatusCode == HttpStatusCode.ServiceUnavailable, "Recuperacion no debe simular envio sin SMTP.");
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            await Task.WhenAll(output, error); // Do not print logs: they can contain auth metadata.
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
