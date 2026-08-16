using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.Application.Contracts.Application;

public interface IJobRetryService
{
    Task<JobResultDto> RetryAsync(string jobId, CancellationToken ct = default);
}
