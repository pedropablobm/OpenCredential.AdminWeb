using System.Text.Json;
using OpenCredential.AdminWeb;
using OpenCredential.AdminWeb.Services;

internal static class UserPagingRegression
{
    public static async Task RunAsync(IAdminRepository repository)
    {
        var career = repository.CreateCareer(new CareerInput { Name = "Paging test", Active = true });
        UserInput Input(string name, bool active) => new()
        {
            Username = name, FirstName = "Paging", LastName = "Test", Email = "paging@example.invalid", DocumentId = "paging-test",
            Active = active, CareerId = career.Id, Password = "Synthetic paging passphrase!", GroupIds = [1]
        };
        repository.CreateUser(Input("paging-alpha", true));
        repository.CreateUser(Input("paging-beta", false));
        repository.CreateUser(Input("paging-literal%_!", true));
        var first = await repository.GetUsersAsync(new(PageSize: 2, Search: "PAGING-"));
        var second = await repository.GetUsersAsync(new(Page: 2, PageSize: 2, Search: "paging-"));
        Assert(first.Total == 3 && first.Items.Count == 2 && second.Items.Count == 1, "Paginacion real y busqueda case-insensitive.");
        Assert(!first.Items.Select(user => user.Id).Intersect(second.Items.Select(user => user.Id)).Any(), "No duplicar usuarios entre paginas.");
        Assert(first.Items.All(user => user.Groups.Any(group => group.Id == 1)), "Grupos de la pagina preservados.");
        var filtered = await repository.GetUsersAsync(new(Search: "paging-", Status: "inactive", CareerId: career.Id));
        Assert(filtered.Total == 1 && filtered.Items.Single().Username == "paging-beta", "Filtros combinados en servidor.");
        var literal = await repository.GetUsersAsync(new(Search: "%_!"));
        Assert(literal.Total == 1 && literal.Items.Single().Username == "paging-literal%_!", "Wildcards tratados como texto literal.");
        Assert((await repository.GetUsersAsync(new(Search: "' OR 1=1 --"))).Total == 0, "Busqueda parametrizada.");
        Assert((await repository.GetUsersAsync(new(Page: 100, PageSize: 2, Search: "paging-"))).Page == 2, "Pagina fuera del total se ajusta.");
        Assert((await repository.GetUsersAsync(new(Search: "no-match-paging"))).TotalPages == 1, "Resultado vacio estable.");
        var serialized = JsonSerializer.Serialize(first);
        Assert(!serialized.Contains("PasswordHash", StringComparison.OrdinalIgnoreCase) && !serialized.Contains("Synthetic"), "Listado sin material de credenciales.");
        foreach (var query in new[] { new UserPageQuery(Page: 0), new UserPageQuery(PageSize: 101), new UserPageQuery(Status: "anything"), new UserPageQuery(Search: new string('x', 201)) })
        {
            var rejected = false;
            try { await repository.GetUsersAsync(query); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "Rechazar parametros invalidos.");
        }
        var cancelled = false;
        try { await repository.GetUsersAsync(new(), new CancellationToken(true)); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "Cancelar peticiones abandonadas.");
        var compact = repository.GetShellSnapshot(includeAudit: false);
        Assert(compact.Users.Count == 0 && compact.UsageRecords.Count == 0 && compact.AuditEntries.Count == 0, "Resumen compacto sin colecciones privadas ni historiales completos.");
        Assert(repository.FindUserByUsername("paging-alpha")?.PasswordHash != null, "El listado no modifica credenciales almacenadas.");
        Assert(repository.GetSnapshot().Users.Count >= 3, "El contrato legacy conserva el snapshot completo.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
