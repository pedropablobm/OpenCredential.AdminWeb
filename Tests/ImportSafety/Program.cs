using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using OpenCredential.AdminWeb.Services;

// Datos y directorio aislados; no se conecta a ningun servidor ni base real.
var root = Path.Combine(Path.GetTempPath(), "oc-import-safety-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("ADMINWEB_DATA_DIR", root);
var repository = new JsonAdminRepository(new TestEnvironment { ContentRootPath = root });
const string password = "synthetic-import-password";
var bytes = Encoding.UTF8.GetBytes($"username,firstname,lastname,email,documentid,password,hashmethod\nimport-synthetic,Test,User,test@example.invalid,123,{password},BCRYPT\n");
using var stream = new MemoryStream(bytes);
var file = new FormFile(stream, 0, bytes.Length, "file", "synthetic.csv");
await repository.ImportUsersAsync(file);
var user = repository.GetSnapshot().Users.Single(item => item.Username == "import-synthetic");
if (!PasswordHashService.VerifyPassword(password, user.PasswordHash, user.HashMethod))
    throw new Exception("JSON debe usar la clave inicial del archivo para un usuario nuevo.");
Console.WriteLine("PASS JSON: importacion conserva la clave inicial indicada.");
var oldHash = user.PasswordHash;
await Import("username,firstname,lastname,email,documentid,password,hashmethod\nimport-synthetic,Updated,User,test@example.invalid,123,,BCRYPT\n");
user = repository.GetSnapshot().Users.Single(item => item.Username == "import-synthetic");
if (user.PasswordHash != oldHash) throw new Exception("Una actualizacion sin clave debe conservar el hash existente.");
await Import("username,firstname,lastname,email,documentid,password,hashmethod\nimport-synthetic,Updated,User,test@example.invalid,123,new-synthetic-password,BCRYPT\n");
user = repository.GetSnapshot().Users.Single(item => item.Username == "import-synthetic");
if (!PasswordHashService.VerifyPassword("new-synthetic-password", user.PasswordHash, user.HashMethod))
    throw new Exception("Una actualizacion con clave debe aplicar la clave indicada.");
Console.WriteLine("PASS JSON: actualizacion con y sin clave.");
try
{
    await Import("username,firstname,password\nno-password,Test,\n");
    throw new Exception("Debe rechazar usuarios nuevos sin clave.");
}
catch (InvalidOperationException)
{
    if (repository.GetSnapshot().Users.Any(item => item.Username == "no-password"))
        throw new Exception("No debe crear al usuario sin clave.");
}
Console.WriteLine("PASS JSON: usuario nuevo sin clave rechazado.");

var table = DelimitedImportParser.Parse("username,password,firstname\r\nuser1,\"  clave,con espacio  \",\"Nombre\nSegunda linea\"\r\n");
if (table.Rows.Count != 1 || table.Rows[0].Fields[1] != "  clave,con espacio  " || table.Rows[0].Fields[2] != "Nombre\nSegunda linea")
    throw new Exception("El analizador debe preservar separadores, espacios y saltos dentro de comillas.");
var tsv = DelimitedImportParser.Parse("username\tpassword\nuser1\t\"abc\"\"def\"\n");
if (tsv.Rows[0].Fields[1] != "abc\"def") throw new Exception("Debe aceptar tabulaciones y comillas escapadas.");
foreach (var invalid in new[] { "username,password,password\nu,p,p", "username,password\nu,\"sin cierre", "username,password\nu,p,extra" })
{
    try { DelimitedImportParser.Parse(invalid); throw new Exception("Debe rechazar archivos malformados."); }
    catch (FormatException) { }
}
Console.WriteLine("PASS analizador: comillas, delimitadores, espacios y archivos malformados.");
var reviewTable = DelimitedImportParser.Parse("username,password,active\nimport-synthetic,,true\nnew-user,synthetic-review-secret,true\nnew-user,another-secret,true\nno-key,,true\n");
var before = repository.GetSnapshot().Users.Count;
var review = ImportReviewService.ReviewUsers(reviewTable, repository.GetSnapshot().Users.Select(item => item.Username));
if (review.CreateCount != 1 || review.UpdateCount != 1 || review.ErrorCount != 2 || repository.GetSnapshot().Users.Count != before)
    throw new Exception("La revision debe clasificar filas sin escribir datos.");
if (System.Text.Json.JsonSerializer.Serialize(review).Contains("synthetic-review-secret", StringComparison.Ordinal))
    throw new Exception("La revision no debe incluir claves.");
try
{
    ImportReviewService.ReviewUsers(DelimitedImportParser.Parse("username,password,clave\nu,p,p"), Array.Empty<string>());
    throw new Exception("Debe rechazar columnas equivalentes.");
}
catch (FormatException) { }
Console.WriteLine("PASS revision: clasificacion sin escrituras ni claves; duplicados rechazados.");

async Task Import(string text)
{
    var content = Encoding.UTF8.GetBytes(text);
    using var input = new MemoryStream(content);
    await repository.ImportUsersAsync(new FormFile(input, 0, content.Length, "file", "synthetic.csv"));
}

sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "ImportSafety";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
