using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using OpenCredential.AdminWeb;
using OpenCredential.AdminWeb.Services;

if (args.Length == 3 && args[0] == "--vm-lab-init")
{
    await VmLab.CreateAsync(args[1], args[2]);
    return 0;
}
if (args.Length == 2 && args[0] == "--transport-child")
{
    await TransportRegression.RunChildAsync(args[1]);
    return 0;
}
if (args.Contains("--transport-only"))
{
    await TransportRegression.RunAsync();
    return 0;
}

var root = Path.Combine(Path.GetTempPath(), "opencredential-regression-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("ADMINWEB_DATA_DIR", root);
Directory.CreateDirectory(root);
var environment = new TestEnvironment { ContentRootPath = root };
var failures = 0;
var count = 0;
await Test("Politica y ciclo de vida invitado sin acceso real", () => { GuestAccessPolicyRegression.Run(); return Task.CompletedTask; });
await Test("Configuracion del despliegue muestra TLS sin exponer credenciales", () =>
{
    var connection = new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = "db.example.invalid", Port = 5432, Database = "synthetic", Username = "test_user",
        Password = "synthetic-only-secret;with-special-characters", SslMode = Npgsql.SslMode.VerifyFull,
        RootCertificate = "/certs/synthetic-ca.crt"
    };
    var service = new DatabaseConfigurationService(Options.Create(new DatabaseOptions
    {
        Mode = "Sql", Provider = "PostgreSql", ConnectionString = connection.ConnectionString, AutoInitialize = false
    }), environment);
    var result = service.GetConfiguration();
    Assert(result.ConnectionDetailsAvailable && result.Host == "db.example.invalid" && result.SslMode == "VerifyFull");
    Assert(result.RootCertificateConfigured == true && result.RootCertificate == "/certs/synthetic-ca.crt" && !result.PasswordConfigured && !result.AutoInitialize);
    Assert(!JsonSerializer.Serialize(result).Contains("synthetic-only-secret", StringComparison.Ordinal));
    var mysqlConnection = new MySqlConnector.MySqlConnectionStringBuilder
    {
        Server = "mysql.example.invalid", Database = "synthetic", UserID = "test_user",
        Password = "synthetic-mysql-only-secret", SslMode = MySqlConnector.MySqlSslMode.Required
    };
    var mysql = new DatabaseConfigurationService(Options.Create(new DatabaseOptions
    {
        Mode = "Sql", Provider = "MySql", ConnectionString = mysqlConnection.ConnectionString, AutoInitialize = false
    }), environment).GetConfiguration();
    Assert(mysql.ConnectionDetailsAvailable && mysql.Host == "mysql.example.invalid" && mysql.SslMode == "Required");
    Assert(mysql.RootCertificateConfigured is null && !mysql.PasswordConfigured);
    Assert(!JsonSerializer.Serialize(mysql).Contains("synthetic-mysql-only-secret", StringComparison.Ordinal));
    var invalid = new DatabaseConfigurationService(Options.Create(new DatabaseOptions
    {
        Mode = "Sql", Provider = "PostgreSql", ConnectionString = "not a connection string"
    }), environment).GetConfiguration();
    Assert(!invalid.ConnectionDetailsAvailable && invalid.SslMode == "");
    return Task.CompletedTask;
});
await Test("JSON conserva credenciales tras reinicio", () =>
{
    var repository = new JsonAdminRepository(environment);
    repository.CreateUser(Input("persistent", "Test-only Passphrase 123!", [1]));
    var restored = new JsonAdminRepository(environment).FindUserByUsername("persistent");
    Assert(PasswordHashService.VerifyPassword("Test-only Passphrase 123!", restored?.PasswordHash, restored?.HashMethod));
    Assert(!JsonSerializer.Serialize(repository.GetSnapshot()).Contains("passwordHash", StringComparison.OrdinalIgnoreCase));
    return Task.CompletedTask;
});
await Test("Editar sin clave conserva algoritmo heredado", () =>
{
    var path = Path.Combine(root, "admin-store.json");
    var snapshot = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
    var node = System.Text.Json.Nodes.JsonNode.Parse(snapshot.GetRawText())!;
    var users = node["users"]!.AsArray();
    var legacy = users.First(item => item!["username"]!.GetValue<string>() == "persistent")!;
    legacy["hashMethod"] = "MD5";
    legacy["passwordHash"] = PasswordHashService.HashPassword("Legacy test", "MD5");
    File.WriteAllText(path, node.ToJsonString());
    var repository = new JsonAdminRepository(environment);
    var user = repository.FindUserByUsername("persistent")!;
    repository.UpdateUser(user.Id, Input("persistent", null));
    user = repository.FindUserByUsername("persistent")!;
    Assert(user.HashMethod == "MD5" && PasswordHashService.VerifyPassword("Legacy test", user.PasswordHash, user.HashMethod));
    return Task.CompletedTask;
});
await Test("Importacion conserva claves y grupos; informa filas invalidas", async () =>
{
    var repository = new JsonAdminRepository(environment);
    var before = repository.FindUserByUsername("persistent")!;
    var result = await repository.ImportUsersAsync(CsvFile("username;firstname;lastname;email;documentid\npersistent;Nuevo;Apellido;test@example.invalid;123\nnew-without-password;N;A;t@example.invalid;124"));
    var after = repository.FindUserByUsername("persistent")!;
    Assert(result.Updated == 1 && result.Imported == 0 && result.Warnings.Count == 1);
    Assert(after.PasswordHash == before.PasswordHash && after.HashMethod == before.HashMethod);
    Assert(after.Groups.Select(g => g.Id).SequenceEqual(before.Groups.Select(g => g.Id)));
});
await Test("Token JSON persiste y solo se consume una vez", () =>
{
    var repository = new JsonAdminRepository(environment);
    var result = repository.RecoverPortalPassword(new PortalPasswordRecoveryInput { Username = "persistent", Email = "test@example.invalid", DocumentId = "123" }, 20);
    Assert(result.Success && result.ResetToken is not null);
    repository = new JsonAdminRepository(environment);
    var input = new PortalPasswordResetWithTokenInput { Token = result.ResetToken!, NewPassword = "Another test password 123!", ConfirmPassword = "Another test password 123!" };
    Assert(repository.ResetPortalPasswordWithToken(input, out _));
    Assert(!repository.ResetPortalPasswordWithToken(input, out _));
    return Task.CompletedTask;
});
await Test("Politicas impiden elevar privilegios y exponer auditoria", () =>
{
    var policy = new UserManagementPolicy(Options.Create(new AdminAuthOptions { Username = "bootstrap" }));
    var coordinator = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, AdminRoles.Coordinator)], "test"));
    var repository = new JsonAdminRepository(environment);
    var normal = repository.FindUserByUsername("persistent")!;
    var privileged = new UserAccount { Id = 99, Username = "privileged", FirstName = "", LastName = "", Email = "", DocumentId = "", Groups = [new() { Id = 7, Name = "AdminWeb-SuperAdmin" }] };
    Assert(policy.CanManage(coordinator, normal));
    Assert(!policy.CanManage(coordinator, privileged));
    Assert(!policy.CanManage(coordinator, null, new UserInput { Username = "new-admin", FirstName = "", LastName = "", Email = "", DocumentId = "", GroupIds = [7] }, repository.GetGroups()));
    Assert(!policy.CanManage(coordinator, null, Input("bootstrap", null)));
    var viewer = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, AdminRoles.Viewer)], "test"));
    var filtered = policy.FilterSnapshot(viewer, repository.GetSnapshot());
    Assert(filtered.Users.Count == 0 && filtered.AuditEntries.Count == 0 && filtered.UsageRecords.Count == 0);
    return Task.CompletedTask;
});
await Test("Previsualizacion no modifica datos y respeta autorizacion", async () =>
{
    var repository = new JsonAdminRepository(environment);
    var before = repository.FindUserByUsername("persistent")!;
    var result = await repository.ImportUsersAsync(CsvFile("username;firstname\npersistent;No guardar"), preview: true);
    Assert(result.Preview && result.Updated == 1 && repository.FindUserByUsername("persistent")!.FirstName == before.FirstName);
    result = await repository.ImportUsersAsync(CsvFile("username;firstname\npersistent;No autorizado"), authorize: _ => false);
    Assert(result.Updated == 0 && result.Warnings.Count == 1);
});
await Test("Cambios de credenciales revocan la version de sesion", () =>
{
    var repository = new JsonAdminRepository(environment);
    var user = repository.FindUserByUsername("persistent")!;
    var version = SessionSecurity.Fingerprint(user);
    repository.ResetUserPassword(user.Id, new PasswordResetInput());
    Assert(version != SessionSecurity.Fingerprint(repository.FindUserByUsername(user.Username)!));
    return Task.CompletedTask;
});
await Test("Claves con espacios y hashes Base64 conservan compatibilidad", () =>
{
    var repository = new JsonAdminRepository(environment);
    repository.CreateUser(Input("spaces", "  Test passphrase 123!  "));
    var user = repository.FindUserByUsername("spaces")!;
    Assert(PasswordHashService.VerifyPassword("  Test passphrase 123!  ", user.PasswordHash, user.HashMethod));
    var hex = PasswordHashService.HashPassword("Legacy", "MD5");
    Assert(PasswordHashService.VerifyPassword("Legacy", Convert.ToBase64String(Convert.FromHexString(hex)), "MD5"));
    Assert(repository.DeactivateUser(user.Id));
    Assert(repository.FindUserByUsername("spaces")?.Active == false);
    return Task.CompletedTask;
});
await Test("JSON: listado paginado, filtros, cancelacion y contrato compacto", () => UserPagingRegression.RunAsync(new JsonAdminRepository(environment)));
await Test("HTTP: autenticacion, roles, CSRF y revocacion", () => HttpRegression.RunAsync(root));
await Test("HTTP: recuperacion administrativa con base caida", () => HttpRegression.RunAsync(root, databaseDown: true));
await Test("HTTP: proxy confiable, cookie Secure y auditoria de IP", () => HttpRegression.RunAsync(root, trustedProxy: true));
for (var index = 0; index + 1 < args.Length; index += 2)
{
    var port = int.Parse(args[index + 1]);
    if (args[index] == "--postgres-port") await Test("PostgreSQL: esquema, transacciones, concurrencia y aislamiento", () => SqlRegression.RunAsync(port));
    else if (args[index] == "--mysql-port") await Test("MySQL: esquema, transacciones, concurrencia y aislamiento", () => SqlRegression.RunAsync(port, mysql: true));
    else throw new ArgumentException("Opcion de pruebas no reconocida.");
}
Console.WriteLine($"Resultado: {count} grupos de regresion, {failures} fallos.");
return failures == 0 ? 0 : 1;

async Task Test(string name, Func<Task> test)
{
    count++;
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures++; Console.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}"); }
}
static void Assert(bool condition) { if (!condition) throw new Exception("Condicion de regresion no satisfecha."); }
static UserInput Input(string name, string? password, List<int>? groups = null) => new() { Username = name, FirstName = "Test", LastName = "User", Email = "test@example.invalid", DocumentId = "123", Active = true, Password = password, GroupIds = groups };
static IFormFile CsvFile(string content)
{
    var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
    return new FormFile(stream, 0, stream.Length, "file", "test.csv");
}
sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "RegressionTests";
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = "";
    public string WebRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
