using Iemas.Application.EmailClassification;
using Iemas.Application.EmailClassification.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

/// <summary>
/// Manual review queue for emails the AI could not decide (ReviewRequired). Supervisor/Manager and
/// above may review; approving an email creates or joins an inquiry through the normal Case path.
/// </summary>
[ApiController]
[Route("api/v1/classification-reviews")]
[Authorize(Policy = "RequireSupervisorOrAbove")]
public class ClassificationReviewController : ControllerBase
{
    private readonly ClassificationReviewService _service;

    public ClassificationReviewController(ClassificationReviewService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PendingReviewDto>>> List([FromQuery] int? take, CancellationToken cancellationToken) =>
        Ok(await _service.ListPendingAsync(take ?? 50, cancellationToken));

    /// <summary>Cheap badge count: how many are waiting, and how many have waited longer than the stale limit.</summary>
    [HttpGet("count")]
    public async Task<ActionResult<PendingReviewCountDto>> Count(CancellationToken cancellationToken) =>
        Ok(await _service.GetPendingCountAsync(cancellationToken));

    [HttpPost("{emailMessageId:guid}/review")]
    public async Task<IActionResult> Review(Guid emailMessageId, [FromBody] ReviewClassificationRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.ReviewAsync(emailMessageId, request, cancellationToken);
        return result.Status switch
        {
            ClassificationReviewStatus.Succeeded => Ok(result.Value),
            ClassificationReviewStatus.NotFound => NotFound(new { message = result.Error }),
            ClassificationReviewStatus.Conflict => Conflict(new { message = result.Error }),
            _ => BadRequest(new { message = result.Error }),
        };
    }
}
