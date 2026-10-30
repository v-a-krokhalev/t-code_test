using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using WebApi.Services;

namespace WebApi.Controllers;

public abstract class BaseApiController : ControllerBase
{
    protected const string AdminRole = "admin";

    protected long CurrentUserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    protected bool IsAdmin => User.IsInRole(AdminRole);

    protected Task<bool> EnsureRateLimitAsync(IRateLimitService rateLimitService)
    {
        return rateLimitService.TryConsumeAsync(CurrentUserId);
    }

    protected IActionResult TooManyRequests()
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests"
        };

        problem.Extensions["reason"] = "rate-limit";
        return StatusCode(StatusCodes.Status429TooManyRequests, problem);
    }

    protected static ProblemDetails CreateConflict(string reason)
    {
        return new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Conflict",
            Detail = reason
        };
    }

    protected static ProblemDetails CreateBadRequest(string title)
    {
        return new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = title
        };
    }
}
