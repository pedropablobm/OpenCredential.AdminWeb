using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using System.Text.Json;
using OpenCredential.AdminWeb;
using OpenCredential.AdminWeb.Services;

if (args.Length is < 1 or > 2 || !int.TryParse(args[0], out var port) || port is < 1 or > 65535)
    throw new ArgumentException("Indica el puerto MySQL descartable en 127.0.0.1 y, opcionalmente, la IP del PostgreSQL sintetico de laboratorio.");

// Run only against a disposable test instance: this creates a database and user.
var suffix = Guid.NewGuid().ToString("N")[..12];
var database = "oc_test_" + suffix;
var user = "oc_" + suffix;
await using (var admin = new MySqlConnection(new MySqlConnectionStringBuilder
{
    Server = "127.0.0.1", Port = (uint)port, UserID = "root", SslMode = MySqlSslMode.None,
    AllowPublicKeyRetrieval = true
}.ConnectionString))
{
    await admin.OpenAsync();
    await using var command = admin.CreateCommand();
    command.CommandText = $"CREATE DATABASE `{database}`; CREATE USER '{user}'@'%' IDENTIFIED BY 'synthetic;value=123'; GRANT ALL ON `{database}`.* TO '{user}'@'%'";
    await command.ExecuteNonQueryAsync();
}

var root = Path.Combine(Path.GetTempPath(), "oc-config-test-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("ADMINWEB_DATA_DIR", root);
var service = new DatabaseConfigurationService(Options.Create(new DatabaseOptions()), new TestEnvironment { ContentRootPath = root });
var deploymentConnection = new NpgsqlConnectionStringBuilder
{
    Host = "db.example.invalid", Database = "synthetic", Username = "test-user", Password = "synthetic-deployment-secret",
    SslMode = SslMode.VerifyFull, RootCertificate = "/certs/synthetic-ca.crt"
};
var deploymentService = new DatabaseConfigurationService(Options.Create(new DatabaseOptions
{
    Mode = "Sql", Provider = "PostgreSql", ConnectionString = deploymentConnection.ConnectionString, AutoInitialize = false
}), new TestEnvironment { ContentRootPath = root });
var deployed = deploymentService.GetConfiguration();
if (!deployed.ConnectionDetailsAvailable || deployed.SslMode != "VerifyFull" || deployed.RootCertificate != "/certs/synthetic-ca.crt" || deployed.PasswordConfigured ||
    JsonSerializer.Serialize(deployed).Contains("synthetic-deployment-secret", StringComparison.Ordinal))
    throw new Exception("Debe mostrar TLS del despliegue sin revelar ni reutilizar su clave.");
var result = await service.TestConnectionAsync(new DatabaseConfigurationInput
{
    Provider = "MySql", Host = "127.0.0.1", Port = port, DatabaseName = database,
    Username = user, Password = "synthetic;value=123", AutoInitialize = false
});
if (!result.Success) throw new Exception("MySQL debe aceptar una clave con delimitadores sin alterar la conexion.");
Console.WriteLine("PASS MySQL: credenciales con caracteres especiales.");

var input = new UserInput
{
    Username = "legacy-user", FirstName = "Test", LastName = "User", Email = "test@example.invalid",
    DocumentId = "123", Active = true, HashMethod = "BCRYPT"
};
var json = new JsonAdminRepository(new TestEnvironment { ContentRootPath = root });
var jsonUser = json.CreateUser(new UserInput
{
    Username = input.Username, FirstName = input.FirstName, LastName = input.LastName,
    Email = input.Email, DocumentId = input.DocumentId, Active = true, Password = "synthetic-test-password"
});
jsonUser.HashMethod = "MD5";
jsonUser.PasswordHash = PasswordHashService.HashPassword("synthetic-test-password", "MD5");
json.UpdateUser(jsonUser.Id, input);
if (jsonUser.HashMethod != "MD5" || !PasswordHashService.VerifyPassword("synthetic-test-password", jsonUser.PasswordHash, jsonUser.HashMethod))
    throw new Exception("JSON debe conservar algoritmo y hash al editar sin clave.");
Console.WriteLine("PASS JSON: edicion conserva credenciales heredadas.");
var replacement = new UserInput
{
    Username = input.Username, FirstName = input.FirstName, LastName = input.LastName,
    Email = input.Email, DocumentId = input.DocumentId, Active = true, HashMethod = "BCRYPT", Password = "replacement-test-password"
};
json.UpdateUser(jsonUser.Id, replacement);
if (jsonUser.HashMethod != "BCRYPT" || !PasswordHashService.VerifyPassword("replacement-test-password", jsonUser.PasswordHash, jsonUser.HashMethod))
    throw new Exception("JSON debe actualizar algoritmo y hash al cambiar la clave.");

var sqlConnection = new MySqlConnectionStringBuilder
{
    Server = "127.0.0.1", Port = (uint)port, UserID = "root", Database = database,
    SslMode = MySqlSslMode.None, AllowPublicKeyRetrieval = true
}.ConnectionString;
await using (var connection = new MySqlConnection(sqlConnection))
{
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "CREATE TABLE users (id INT PRIMARY KEY, username VARCHAR(100), first_name VARCHAR(100), last_name VARCHAR(100), email VARCHAR(200), document_id VARCHAR(50), status INT, career_id INT NULL, level_id INT NULL, hash_method VARCHAR(40), password_hash TEXT, failed_attempts INT DEFAULT 0, locked_until DATETIME NULL, last_attempt_at DATETIME NULL); INSERT INTO users (id, username, first_name, last_name, email, document_id, status, hash_method, password_hash) VALUES (1, 'legacy-user', 'Test', 'User', 'test@example.invalid', '123', 1, 'MD5', @hash)";
    command.Parameters.AddWithValue("@hash", PasswordHashService.HashPassword("synthetic-test-password", "MD5"));
    await command.ExecuteNonQueryAsync();
    command.CommandText = "CREATE TABLE portal_password_reset_tokens (id INT PRIMARY KEY, user_id INT, username VARCHAR(100), email VARCHAR(200), reset_token VARCHAR(128), created_utc DATETIME, expires_utc DATETIME, consumed_utc DATETIME NULL)";
    await command.ExecuteNonQueryAsync();
}
var sql = new SqlAdminRepository(Options.Create(new DatabaseOptions { Mode = "Sql", Provider = "MySql", ConnectionString = sqlConnection, AutoInitialize = false }));
var updated = sql.UpdateUser(1, input);
if (updated?.HashMethod != "MD5") throw new Exception("La respuesta MySQL debe indicar el algoritmo conservado.");
await using (var connection = new MySqlConnection(sqlConnection))
{
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT hash_method, password_hash FROM users WHERE id = 1";
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync() || reader.GetString(0) != "MD5" || !PasswordHashService.VerifyPassword("synthetic-test-password", reader.GetString(1), reader.GetString(0)))
        throw new Exception("MySQL debe conservar algoritmo y hash al editar sin clave.");
}
Console.WriteLine("PASS MySQL: edicion conserva credenciales heredadas.");
if (sql.UpdateUser(1, replacement)?.HashMethod != "BCRYPT") throw new Exception("MySQL debe actualizar el algoritmo al cambiar la clave.");
var sqlReplacement = sql.FindUserByUsername(input.Username);
if (sqlReplacement is null || !PasswordHashService.VerifyPassword("replacement-test-password", sqlReplacement.PasswordHash, sqlReplacement.HashMethod))
    throw new Exception("MySQL debe guardar el nuevo hash.");

if (args.Length == 2)
{
    // Explicitly target the disposable PostgreSQL lab configured for this regression, never a real database.
    if (args[1] != "172.17.0.1") throw new ArgumentException("IP de laboratorio PostgreSQL no esperada.");
    var pgDatabase = "oc_test_" + suffix;
    var pgUser = "oc_" + suffix;
    var pgAdmin = new NpgsqlConnectionStringBuilder
    {
        Host = args[1], Port = 15432, Database = "postgres", Username = "synthetic",
        Password = "synthetic-only-password", SslMode = SslMode.Require
    };
    await using (var connection = new NpgsqlConnection(pgAdmin.ConnectionString))
    {
        await connection.OpenAsync();
        await using var createRole = connection.CreateCommand();
        createRole.CommandText = $"CREATE ROLE \"{pgUser}\" LOGIN PASSWORD 'synthetic;value=123'";
        await createRole.ExecuteNonQueryAsync();
        await using var createDb = connection.CreateCommand();
        createDb.CommandText = $"CREATE DATABASE \"{pgDatabase}\"";
        await createDb.ExecuteNonQueryAsync();
    }
    var pgResult = await service.TestConnectionAsync(new DatabaseConfigurationInput
    {
        Provider = "PostgreSql", Host = args[1], Port = 15432, DatabaseName = pgDatabase,
        Username = pgUser, Password = "synthetic;value=123", SslMode = "Require", AutoInitialize = false
    });
    if (!pgResult.Success) throw new Exception("PostgreSQL debe aceptar claves con delimitadores.");
    var verifiedJson = JsonSerializer.Serialize(new
    {
        provider = "PostgreSql", host = args[1], port = 15432, databaseName = pgDatabase,
        username = pgUser, password = "synthetic;value=123", sslMode = "VerifyFull",
        rootCertificate = "/certs/postgres-ca.crt", autoInitialize = false
    });
    var verifiedInput = JsonSerializer.Deserialize<DatabaseConfigurationInput>(verifiedJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    var legacyInput = new DatabaseConfigurationInput
    {
        Provider = "PostgreSql", Host = args[1], Port = 15432, DatabaseName = pgDatabase,
        Username = pgUser, Password = "synthetic;value=123", SslMode = "Disable", AutoInitialize = false
    };
    if (!(await service.SaveConfigurationAsync(legacyInput)).Success || service.GetConfiguration().SslMode != "Disable")
        throw new Exception("Debe reconocer la configuracion heredada antes de corregirla.");
    var verifiedTest = await service.TestConnectionAsync(verifiedInput);
    if (!verifiedTest.Success) throw new Exception("PostgreSQL VerifyFull debe aceptar la CA sintetica y la IP correcta.");
    var saved = await service.SaveConfigurationAsync(verifiedInput);
    if (!saved.Success) throw new Exception("Debe guardar la conexion TLS verificada.");
    var publicConfig = service.GetConfiguration();
    var publicJson = JsonSerializer.Serialize(publicConfig);
    if (publicJson.Contains("synthetic;value=123", StringComparison.Ordinal)) throw new Exception("La configuracion no debe exponer la clave.");
    var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "adminweb-runtime.json")));
    var savedConnection = new NpgsqlConnectionStringBuilder(persisted.RootElement.GetProperty("database").GetProperty("connectionString").GetString());
    if (savedConnection.SslMode != SslMode.VerifyFull || savedConnection.RootCertificate != "/certs/postgres-ca.crt")
        throw new Exception("La configuracion persistida debe conservar VerifyFull y la CA.");
    var badJson = verifiedJson.Replace("/certs/postgres-ca.crt", "/certs/missing.crt", StringComparison.Ordinal);
    var badInput = JsonSerializer.Deserialize<DatabaseConfigurationInput>(badJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    var previousDocument = await File.ReadAllTextAsync(Path.Combine(root, "adminweb-runtime.json"));
    if ((await service.SaveConfigurationAsync(badInput)).Success) throw new Exception("No debe guardar una CA incorrecta.");
    if (previousDocument != await File.ReadAllTextAsync(Path.Combine(root, "adminweb-runtime.json"))) throw new Exception("Un fallo no debe sobrescribir la configuracion anterior.");
    var wrongHostJson = verifiedJson.Replace(args[1], "wrong-postgres.invalid", StringComparison.Ordinal);
    var wrongHostInput = JsonSerializer.Deserialize<DatabaseConfigurationInput>(wrongHostJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    if ((await service.TestConnectionAsync(wrongHostInput)).Success) throw new Exception("VerifyFull debe rechazar un nombre incorrecto.");
    var sameJson = verifiedJson.Replace("synthetic;value=123", "", StringComparison.Ordinal);
    var sameInput = JsonSerializer.Deserialize<DatabaseConfigurationInput>(sameJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    if (!(await service.TestConnectionAsync(sameInput)).Success) throw new Exception("Debe reutilizar la clave para el mismo destino.");
    var otherJson = sameJson.Replace(pgDatabase, "postgres", StringComparison.Ordinal);
    var otherInput = JsonSerializer.Deserialize<DatabaseConfigurationInput>(otherJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    if ((await service.TestConnectionAsync(otherInput)).Success) throw new Exception("No debe reutilizar la clave para otra base.");
    Console.WriteLine("PASS PostgreSQL: CA verificada, guardado seguro y clave ligada al destino.");
    pgAdmin.Database = pgDatabase;
    await using (var connection = new NpgsqlConnection(pgAdmin.ConnectionString))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE users (id INT PRIMARY KEY, username VARCHAR(100), first_name VARCHAR(100), last_name VARCHAR(100), email VARCHAR(200), document_id VARCHAR(50), status INT, career_id INT NULL, level_id INT NULL, hash_method VARCHAR(40), password_hash TEXT, failed_attempts INT DEFAULT 0, locked_until TIMESTAMP NULL, last_attempt_at TIMESTAMP NULL); INSERT INTO users (id, username, first_name, last_name, email, document_id, status, hash_method, password_hash) VALUES (1, 'legacy-user', 'Test', 'User', 'test@example.invalid', '123', 1, 'MD5', @hash)";
        command.Parameters.AddWithValue("hash", PasswordHashService.HashPassword("synthetic-test-password", "MD5"));
        await command.ExecuteNonQueryAsync();
        command.CommandText = "CREATE TABLE portal_password_reset_tokens (id INT PRIMARY KEY, user_id INT, username VARCHAR(100), email VARCHAR(200), reset_token VARCHAR(128), created_utc TIMESTAMP, expires_utc TIMESTAMP, consumed_utc TIMESTAMP NULL)";
        await command.ExecuteNonQueryAsync();
    }
    var pg = new SqlAdminRepository(Options.Create(new DatabaseOptions { Mode = "Sql", Provider = "PostgreSql", ConnectionString = pgAdmin.ConnectionString, AutoInitialize = false }));
    if (pg.UpdateUser(1, input)?.HashMethod != "MD5") throw new Exception("La respuesta PostgreSQL debe indicar el algoritmo conservado.");
    await using (var connection = new NpgsqlConnection(pgAdmin.ConnectionString))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT hash_method, password_hash FROM users WHERE id = 1";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.GetString(0) != "MD5" || !PasswordHashService.VerifyPassword("synthetic-test-password", reader.GetString(1), reader.GetString(0)))
            throw new Exception("PostgreSQL debe conservar algoritmo y hash al editar sin clave.");
    }
    if (pg.UpdateUser(1, replacement)?.HashMethod != "BCRYPT") throw new Exception("PostgreSQL debe actualizar el algoritmo al cambiar la clave.");
    var pgReplacement = pg.FindUserByUsername(input.Username);
    if (pgReplacement is null || !PasswordHashService.VerifyPassword("replacement-test-password", pgReplacement.PasswordHash, pgReplacement.HashMethod))
        throw new Exception("PostgreSQL debe guardar el nuevo hash.");
    Console.WriteLine("PASS PostgreSQL: credenciales con delimitadores y edicion heredada.");
    ExercisePasswordSpaces(pg, "pg");
}

ExercisePasswordSpaces(json, "json");
ExercisePasswordSpaces(sql, "mysql");

static void ExercisePasswordSpaces(IAdminRepository repository, string suffix)
{
    var username = "space-" + suffix;
    var created = repository.CreateUser(new UserInput
    {
        Username = username, FirstName = "Test", LastName = "User", Email = "test@example.invalid",
        DocumentId = "456", Active = true, Password = " initial test password "
    });
    Check(" initial test password ");
    repository.UpdateUser(created.Id, new UserInput
    {
        Username = username, FirstName = "Test", LastName = "User", Email = "test@example.invalid",
        DocumentId = "456", Active = true, Password = " updated test password "
    });
    Check(" updated test password ");
    var reset = repository.ResetUserPassword(created.Id, new PasswordResetInput { Generate = false, Password = " reset test password " });
    if (reset?.GeneratedPassword != " reset test password ") throw new Exception(suffix + ": el resultado debe conservar espacios al restablecer.");
    Check(" reset test password ");
    var direct = repository.UpdatePasswordByUsername(username, " portal test password ", "BCRYPT");
    if (direct?.GeneratedPassword != " portal test password ") throw new Exception(suffix + ": el resultado del portal debe conservar espacios.");
    Check(" portal test password ");
    var recovery = repository.RecoverPortalPassword(new PortalPasswordRecoveryInput
    {
        Username = username, DocumentId = "456", Email = "test@example.invalid"
    }, 20);
    if (!recovery.Success || string.IsNullOrEmpty(recovery.ResetToken)) throw new Exception(suffix + ": no se genero token de prueba.");
    var resetInput = new PortalPasswordResetWithTokenInput
    {
        Token = recovery.ResetToken, NewPassword = " recovery test password ", ConfirmPassword = " recovery test password "
    };
    if (!repository.ResetPortalPasswordWithToken(resetInput, out _)) throw new Exception(suffix + ": fallo el restablecimiento de prueba.");
    Check(" recovery test password ");
    Console.WriteLine("PASS " + suffix + ": espacios conservados en claves.");

    void Check(string password)
    {
        var stored = repository.FindUserByUsername(username);
        if (stored is null || !PasswordHashService.VerifyPassword(password, stored.PasswordHash, stored.HashMethod) ||
            PasswordHashService.VerifyPassword(password.Trim(), stored.PasswordHash, stored.HashMethod))
            throw new Exception(suffix + ": la clave debe verificarse exactamente, incluidos espacios.");
    }
}

sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "ConnectionRegression";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
