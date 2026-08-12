using Loregrove.Application.Persistence;
using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Loregrove.Infrastructure.Sqlite.Persistence;

public sealed class LoregroveDbContext(DbContextOptions<LoregroveDbContext> options)
    : DbContext(options), ILoregroveDbContext
{
    public DbSet<SourceDocument> SourceDocuments => Set<SourceDocument>();

    public DbSet<SourceDocumentVersion> SourceDocumentVersions => Set<SourceDocumentVersion>();

    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();

    public DbSet<ParsedArtifact> ParsedArtifacts => Set<ParsedArtifact>();

    public DbSet<SourceAnchor> SourceAnchors => Set<SourceAnchor>();

    public async Task<ILoregroveDbTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new LoregroveDbTransaction(
            await Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false));

    public void ClearTrackedChanges() => ChangeTracker.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LoregroveDbContext).Assembly);
    }

    private sealed class LoregroveDbTransaction(IDbContextTransaction transaction) : ILoregroveDbTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken) =>
            transaction.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
