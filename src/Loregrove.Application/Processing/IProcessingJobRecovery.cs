namespace Loregrove.Application.Processing;

public interface IProcessingJobRecovery
{
    Task<int> RecoverInterruptedJobsAsync(CancellationToken cancellationToken);
}
