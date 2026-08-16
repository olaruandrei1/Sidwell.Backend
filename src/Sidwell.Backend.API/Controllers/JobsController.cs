using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sidwell.Backend.API.Auth;
using Sidwell.Backend.Application.Contracts.Application;
using Sidwell.Backend.Application.Dtos;

namespace Sidwell.Backend.API.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = SessionTokenDefaults.AuthenticationScheme)]
public sealed class JobsController(IJobRetryService jobRetryService) : ControllerBase
{
    [HttpPost("jobs/{id}/retry")]
    public async Task<ActionResult<JobResultDto>> Retry(string id, CancellationToken ct) =>
        Ok(await jobRetryService.RetryAsync(id, ct));
}
