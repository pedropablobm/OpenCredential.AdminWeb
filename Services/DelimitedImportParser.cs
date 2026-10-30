using System.Text;

namespace OpenCredential.AdminWeb.Services;

// Analiza sin escribir ni registrar valores: las celdas pueden contener claves.
public static class DelimitedImportParser
{
    public sealed record Row(int Line, IReadOnlyList<string> Fields);
    public sealed record Table(IReadOnlyList<string> Headers, IReadOnlyList<Row> Rows);

    public static Table Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > 5_000_000) throw new FormatException("El archivo supera el limite de lectura.");
        content = content.TrimStart('\uFEFF');
        var delimiter = DetectDelimiter(content);
        var rows = new List<Row>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closed = false;
        var line = 1;
        var rowLine = 1;

        void FinishField() { fields.Add(field.ToString()); field.Clear(); closed = false; }
        void FinishRow()
        {
            FinishField();
            if (fields.Any(value => !string.IsNullOrWhiteSpace(value)))
                rows.Add(new Row(rowLine, fields.ToArray()));
            if (rows.Count > 10001) throw new FormatException("El archivo supera el limite de filas.");
            fields.Clear();
        }

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; }
                    else { quoted = false; closed = true; }
                }
                else if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n') { field.Append("\r\n"); i++; line++; }
                else { field.Append(c); if (c is '\n' or '\r') line++; }
            }
            else if (c == delimiter) FinishField();
            else if (c is '\r' or '\n')
            {
                FinishRow();
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                rowLine = ++line;
            }
            else if (c == '"' && field.Length == 0 && !closed) quoted = true;
            else if (c == '"' || closed) throw new FormatException($"Formato invalido en la linea {line}.");
            else field.Append(c);
        }
        if (quoted) throw new FormatException($"Comillas sin cerrar en la linea {rowLine}.");
        if (field.Length > 0 || fields.Count > 0 || closed) FinishRow();
        if (rows.Count == 0) throw new FormatException("El archivo esta vacio.");
        var headers = rows[0].Fields.Select(RepositorySupport.NormalizeHeader).ToArray();
        if (headers.Any(string.IsNullOrEmpty) || headers.Distinct(StringComparer.Ordinal).Count() != headers.Length)
            throw new FormatException("Los encabezados estan vacios o duplicados.");
        if (!headers.Contains("username")) throw new FormatException("Falta el encabezado username.");
        foreach (var row in rows.Skip(1))
            if (row.Fields.Count != headers.Length) throw new FormatException($"Numero incorrecto de columnas en la linea {row.Line}.");
        return new Table(headers, rows.Skip(1).ToArray());
    }

    private static char DetectDelimiter(string content)
    {
        var counts = new int[4];
        var candidates = new[] { ',', ';', '\t', '|' };
        var quoted = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '"')
            {
                if (quoted && i + 1 < content.Length && content[i + 1] == '"') { i++; continue; }
                quoted = !quoted;
            }
            else if (!quoted && c is '\r' or '\n') break;
            else if (!quoted)
            {
                var index = Array.IndexOf(candidates, c);
                if (index >= 0) counts[index]++;
            }
        }
        var maximum = counts.Max();
        if (maximum == 0 || counts.Count(count => count == maximum) != 1)
            throw new FormatException("No se pudo determinar un separador unico en el encabezado.");
        return candidates[Array.IndexOf(counts, maximum)];
    }
}
