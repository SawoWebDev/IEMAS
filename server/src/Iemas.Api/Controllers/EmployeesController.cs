using Iemas.Application.Employees;
using Iemas.Application.Employees.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

[ApiController]
[Route("api/v1/employees")]
[Authorize]
public class EmployeesController : ControllerBase
{
    private readonly EmployeeService _employeeService;
    private readonly Iemas.Application.Inquiries.PracticeInquiryService _practice;

    public EmployeesController(EmployeeService employeeService, Iemas.Application.Inquiries.PracticeInquiryService practice)
    {
        _employeeService = employeeService;
        _practice = practice;
    }

    /// <summary>
    /// Sends a practice inquiry to this employee's monitored mailbox: a sample customer email that runs through
    /// the real pop-up, reminder, warning and escalation steps within minutes. Management is never emailed.
    /// </summary>
    [HttpPost("{id:guid}/practice-inquiry")]
    [Authorize(Policy = "RequireAdministrator")]
    public async Task<ActionResult<Iemas.Application.Inquiries.PracticeInquiryResult>> SendPracticeInquiry(Guid id, CancellationToken cancellationToken)
    {
        var result = await _practice.SendAsync(id, User.Identity?.Name ?? "an administrator", cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }

    /// <summary>How far this person's latest practice inquiry has got (next-next list ending in the escalation).</summary>
    [HttpGet("{id:guid}/practice-progress")]
    [Authorize(Policy = "RequireAdministrator")]
    public async Task<ActionResult<Iemas.Application.Inquiries.PracticeProgressDto>> PracticeProgress(Guid id, CancellationToken cancellationToken)
        => Ok(await _practice.GetProgressAsync(id, cancellationToken));

    /// <summary>Sends a sample escalation email to this person, to prove a supervisor receives escalations.</summary>
    [HttpPost("{id:guid}/test-escalation-receipt")]
    [Authorize(Policy = "RequireAdministrator")]
    public async Task<ActionResult<Iemas.Application.Escalations.TestEscalationEmailResult>> TestEscalationReceipt(
        Guid id, [FromServices] Iemas.Application.Escalations.EscalationTestEmailService testEmail, CancellationToken cancellationToken)
    {
        var result = await testEmail.SendToRecipientAsync(id, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { message = result.Error });
    }

    [HttpGet]
    [Authorize(Policy = "RequireAuditorOrAbove")]
    public async Task<ActionResult<List<EmployeeDto>>> GetAll(CancellationToken cancellationToken)
    {
        return Ok(await _employeeService.GetAllAsync(cancellationToken));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "RequireAuditorOrAbove")]
    public async Task<ActionResult<EmployeeDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var employee = await _employeeService.GetByIdAsync(id, cancellationToken);
        return employee is null ? NotFound() : Ok(employee);
    }

    [HttpPost]
    [Authorize(Policy = "RequireAdministrator")]
    public async Task<ActionResult<EmployeeDto>> Create([FromBody] CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        var result = await _employeeService.CreateAsync(request, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { message = result.Error });
        }

        return CreatedAtAction(nameof(GetById), new { id = result.Value!.Id }, result.Value);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "RequireAdministrator")]
    public async Task<ActionResult<EmployeeDto>> Update(Guid id, [FromBody] UpdateEmployeeRequest request, CancellationToken cancellationToken)
    {
        var result = await _employeeService.UpdateAsync(id, request, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Employee not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }

        return Ok(result.Value);
    }

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = "RequireAdministrator")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken)
    {
        var result = await _employeeService.SetActiveAsync(id, false, cancellationToken);
        if (!result.Succeeded)
        {
            return NotFound(new { message = result.Error });
        }

        return NoContent();
    }

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = "RequireAdministrator")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken cancellationToken)
    {
        var result = await _employeeService.SetActiveAsync(id, true, cancellationToken);
        if (!result.Succeeded)
        {
            return NotFound(new { message = result.Error });
        }

        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "RequireAdministrator")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _employeeService.DeleteAsync(id, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Employee not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }

        return NoContent();
    }
}
