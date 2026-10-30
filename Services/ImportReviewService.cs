namespace OpenCredential.AdminWeb.Services;

// Solo clasifica; no almacena el archivo, las claves ni modifica el repositorio.
public static class ImportReviewService
{
    public sealed record RowReview(int Line, string Username, string Action, IReadOnlyList<string> Errors);
    public sealed record Review(IReadOnlyList<RowReview> Rows, int CreateCount, int UpdateCount, int ErrorCount);

    private static readonly string[][] Aliases =
    [
        ["username"], ["firstname", "nombres", "nombre"], ["lastname", "apellidos", "apellido"],
        ["email", "correo"], ["documentid", "documento", "cedula"], ["career", "carrera"],
        ["semester", "semestre", "level"], ["active", "estado", "status"],
        ["hashmethod", "algoritmo"], ["password", "clave", "contrasena"]
    ];

    public static Review ReviewUsers(DelimitedImportParser.Table table, IEnumerable<string> existingUsernames)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(existingUsernames);
        if (table.Rows.Count == 0) throw new FormatException("El archivo no contiene filas de usuarios.");
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < table.Headers.Count; i++)
        {
            var header = table.Headers[i];
            var aliases = Aliases.FirstOrDefault(group => group.Contains(header));
            if (aliases is null) throw new FormatException("El archivo contiene un encabezado no reconocido.");
            if (!columns.TryAdd(aliases[0], i)) throw new FormatException("El archivo contiene columnas equivalentes duplicadas.");
        }
        if (!columns.ContainsKey("username")) throw new FormatException("Falta el encabezado username.");
        var existing = new HashSet<string>(existingUsernames, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<RowReview>();
        foreach (var row in table.Rows)
        {
            string Value(string key) => columns.TryGetValue(key, out var index) ? row.Fields[index] : "";
            var username = Value("username").Trim();
            var errors = new List<string>();
            if (username.Length == 0) errors.Add("El usuario es obligatorio.");
            else if (!seen.Add(username)) errors.Add("El usuario aparece mas de una vez en el archivo.");
            var update = existing.Contains(username);
            var password = Value("password");
            if (!update && string.IsNullOrWhiteSpace(password)) errors.Add("El usuario nuevo requiere una clave inicial.");
            if (password == "<DEFINIR_CLAVE_SEGURA>") errors.Add("Sustituye el marcador de clave de la plantilla.");
            var active = Value("active").Trim().ToLowerInvariant();
            if (active.Length > 0 && !new[] { "1", "0", "true", "false", "activo", "inactivo", "active", "inactive", "si", "yes", "no" }.Contains(active))
                errors.Add("El estado indicado no es valido.");
            results.Add(new RowReview(row.Line, username, errors.Count > 0 ? "error" : update ? "actualizar" : "crear", errors));
        }
        return new Review(results, results.Count(row => row.Action == "crear"), results.Count(row => row.Action == "actualizar"), results.Count(row => row.Action == "error"));
    }
}
