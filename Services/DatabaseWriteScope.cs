using System.Data.Common;
using System.Runtime.CompilerServices;

namespace OpenCredential.AdminWeb.Services;

// Attaches every repository command on this connection to its explicit unit of work.
// Connections are per operation, never shared between requests.
internal sealed class DatabaseWriteScope : IDisposable
{
    private static readonly ConditionalWeakTable<DbConnection, DbTransaction> Transactions = new();
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    public DatabaseWriteScope(DbConnection connection)
    {
        _connection = connection;
        _transaction = connection.BeginTransaction();
        Transactions.Add(connection, _transaction);
    }
    public static void Enlist(DbConnection connection, DbCommand command)
    {
        if (Transactions.TryGetValue(connection, out var transaction)) command.Transaction = transaction;
    }
    public void Commit()
    {
        _transaction.Commit();
        Transactions.Remove(_connection);
    }
    public void Dispose()
    {
        Transactions.Remove(_connection);
        _transaction.Dispose();
    }
}
