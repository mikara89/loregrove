using Loregrove.Application.Persistence;
using Loregrove.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace Loregrove.Application.Processing;

public sealed class ProcessingJobRecovery(
    ILoregroveDbContext dbContext,
    TimeProvider? timeProvider = null) : IProcessingJobRecovery
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<int> RecoverInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        var recoveredAt = _timeProvider.GetUtcNow();
        return dbContext.ProcessingJobs
            .Where(job => job.State == ProcessingJobState.Processing)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.State, ProcessingJobState.Pending)
                    .SetProperty(job => job.UpdatedAt, recoveredAt),
                cancellationToken);
    }
}
