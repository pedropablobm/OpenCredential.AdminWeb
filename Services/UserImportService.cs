using System.Data.Common;
using System.Text;

namespace OpenCredential.AdminWeb.Services;

internal static class UserImportService
{
    public static async Task<ImportUsersResult> ImportAsync(IFormFile file, IAdminRepository repository,
        Func<string?, int?> ensureCareer, Func<string?, int?> ensureSemester,
        Func<UserAccount?, bool>? authorize, bool preview)
    {
        var warnings = new List<string>();
        if (file.Length > 2 * 1024 * 1024)
            throw new InvalidOperationException("El archivo no puede superar 2 MiB.");
        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, true);
        var content = await reader.ReadToEndAsync();
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines.Length > 10001)
            throw new InvalidOperationException("El archivo debe contener cabecera y hasta 10000 filas.");
        var delimiter = RepositorySupport.DetectDelimiter(lines[0]);
        var headers = RepositorySupport.SplitLine(lines[0], delimiter).Select(RepositorySupport.NormalizeHeader).ToList();
        if (headers.Distinct().Count() != headers.Count || !headers.Contains("username"))
            throw new InvalidOperationException("La cabecera requiere username y no puede tener columnas repetidas.");
        var map = headers.Select((name, index) => (name, index)).ToDictionary(item => item.name, item => item.index);
        var imported = 0;
        var updated = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            try
            {
                var values = RepositorySupport.SplitLine(lines[i], delimiter);
                var username = RepositorySupport.GetValue(values, map, "username").Trim();
                if (string.IsNullOrWhiteSpace(username)) throw new InvalidOperationException("username vacio.");
                if (!seen.Add(username)) throw new InvalidOperationException("Usuario repetido dentro del archivo.");
                var existing = repository.FindUserByUsername(username);
                if (authorize is not null && !authorize(existing))
                    throw new InvalidOperationException("No tienes permiso para modificar esta cuenta.");
                string Value(string? fallback, params string[] names) => names.Any(map.ContainsKey)
                    ? RepositorySupport.GetValue(values, map, names) : fallback ?? "";
                var password = RepositorySupport.GetValue(values, map, "password", "clave", "contrasena");
                if (existing is null && string.IsNullOrWhiteSpace(password))
                    throw new InvalidOperationException("Los usuarios nuevos requieren una clave inicial.");
                if (!string.IsNullOrEmpty(password)) PasswordHashService.ValidateNewPassword(password);
                var careerName = RepositorySupport.GetValue(values, map, "career", "carrera");
                var semesterName = RepositorySupport.GetValue(values, map, "semester", "semestre", "level");
                var input = new UserInput
                {
                    Username = username,
                    FirstName = Value(existing?.FirstName, "firstname", "nombres", "nombre"),
                    LastName = Value(existing?.LastName, "lastname", "apellidos", "apellido"),
                    Email = Value(existing?.Email, "email", "correo"),
                    DocumentId = Value(existing?.DocumentId, "documentid", "documento", "cedula"),
                    Active = RepositorySupport.ParseBoolean(Value(existing?.Active.ToString() ?? "true", "active", "estado", "status"), true),
                    CareerId = string.IsNullOrWhiteSpace(careerName) || preview ? existing?.CareerId : ensureCareer(careerName),
                    SemesterId = string.IsNullOrWhiteSpace(semesterName) || preview ? existing?.SemesterId : ensureSemester(semesterName),
                    Password = string.IsNullOrEmpty(password) ? null : password,
                    GroupIds = null
                };
                UserInputValidation.Validate(input);
                if (!preview)
                {
                    if (existing is null) repository.CreateUser(input);
                    else repository.UpdateUser(existing.Id, input);
                }
                if (existing is null) imported++; else updated++;
            }
            catch (InvalidOperationException exception) { warnings.Add($"Fila {i + 1}: {exception.Message}"); }
            catch (DbException) { warnings.Add($"Fila {i + 1}: no se pudo guardar; revisa duplicados y referencias."); }
        }
        return new ImportUsersResult { Imported = imported, Updated = updated, Warnings = warnings, Preview = preview };
    }
}
