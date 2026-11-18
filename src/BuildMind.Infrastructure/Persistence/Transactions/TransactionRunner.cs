using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BuildMind.Infrastructure.Persistence.Transactions;

/// <summary>Source of database transactions; the seam keeps <see cref="TransactionRunner"/> testable without a database.</summary>
internal interface IDbTransactionSource
{
    bool HasActiveTransaction { get; }

    Task<IDbContextTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets all tracked entities. After a rollback or an unknown commit outcome, tracked state no longer
    /// matches the database (EF may already have accepted the changes), so it must not be saved again.
    /// </summary>
    void DiscardTrackedChanges();
}

internal sealed class DbContextTransactionSource(BuildMindDbContext context) : IDbTransactionSource
{
    public bool HasActiveTransaction => context.Database.CurrentTransaction is not null;

    public void DiscardTrackedChanges() => context.ChangeTracker.Clear();

    public Task<IDbContextTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken) =>
        context.Database.BeginTransactionAsync(isolationLevel, cancellationToken);
}

/// <summary>
/// The commit result of a transaction is unknown: the commit request failed after it was sent, so the
/// changes may or may not be durable. Callers must re-read state (or use an idempotency key) and must
/// never blindly retry.
/// </summary>
internal sealed class TransactionOutcomeUnknownException(Exception innerException)
    : Exception("The database transaction outcome is unknown.", innerException);

/// <summary>
/// Minimal internal helper that runs one atomic persistence operation in one database transaction
/// (BE-M4-D02a). It is not a unit-of-work or repository abstraction and never retries.
/// </summary>
/// <remarks>
/// Semantics: begin → operation → commit. Any exception from the operation (including cancellation)
/// rolls back and is rethrown unchanged. Cancellation is honoured until commit starts; commit itself is not
/// cancelled midway, and a failed commit raises <see cref="TransactionOutcomeUnknownException"/>.
/// The transaction is always disposed. Nested use on the same context is rejected rather than silently joined.
/// Operations must not call external services inside the transaction. After a rollback or an unknown commit
/// outcome all tracked entities are discarded, so stale state can never be saved later in the same scope;
/// callers re-read what they need. A retry is the caller's decision and requires an idempotent operation
/// (see <c>PersistenceFailure.IsTransient</c>).
/// </remarks>
internal sealed class TransactionRunner(IDbTransactionSource source)
{
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (source.HasActiveTransaction)
        {
            throw new InvalidOperationException("A database transaction is already active on this context.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        await using var transaction = await source.BeginTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);

        T result;
        try
        {
            result = await operation(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            await RollBackQuietlyAsync(transaction).ConfigureAwait(false);
            source.DiscardTrackedChanges();
            throw;
        }

        try
        {
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            source.DiscardTrackedChanges();
            throw new TransactionOutcomeUnknownException(exception);
        }

        return result;
    }

    public Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return ExecuteAsync<bool>(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken,
            isolationLevel);
    }

    private static async Task RollBackQuietlyAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original exception is what callers need. If rollback fails (for example the connection
            // dropped), the server discards the uncommitted transaction and disposal releases it.
        }
    }
}
