using Iemas.Application.EmailAccounts;
using Iemas.Application.EmailAccounts.Dtos;
using Iemas.Domain.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Iemas.Api.Controllers;

[ApiController]
[Route("api/v1/email-accounts")]
[Authorize(Policy = "RequireAdministrator")]
public class EmailAccountsController : ControllerBase
{
    private readonly EmailAccountService _emailAccountService;

    public EmailAccountsController(EmailAccountService emailAccountService)
    {
        _emailAccountService = emailAccountService;
    }

    [HttpGet]
    public async Task<ActionResult<List<EmailAccountDto>>> GetAll([FromQuery] EmailAccountPurpose? purpose, CancellationToken cancellationToken)
    {
        return Ok(await _emailAccountService.GetAllAsync(purpose, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EmailAccountDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var account = await _emailAccountService.GetByIdAsync(id, cancellationToken);
        return account is null ? NotFound() : Ok(account);
    }

    /// <summary>Finds the IMAP server for an address by logging in with the typed password (nothing is saved).</summary>
    [HttpPost("detect-settings")]
    public async Task<IActionResult> DetectSettings([FromBody] DetectMailSettingsRequest request, [FromServices] Iemas.Application.Agents.MailServerDiscovery discovery, CancellationToken cancellationToken)
    {
        var email = (request.EmailAddress ?? "").Trim().ToLowerInvariant();
        if (email.IndexOf('@') < 1 || string.IsNullOrEmpty(request.Password))
            return BadRequest(new { message = "Enter the email address and its password." });
        var found = await discovery.DiscoverAsync(email, request.Password, cancellationToken);
        if (found.Succeeded)
            return Ok(new { host = found.Server!.Host, port = found.Server.Port, encryption = found.Server.Encryption, username = found.Username });
        return BadRequest(new { message = found.WrongPassword
            ? "The mail server refused that password. For Gmail or Microsoft 365 use an app password."
            : "Could not find the mail server for this address. Enter the server settings by hand on the Mailboxes tab." });
    }

    [HttpPost]
    public async Task<ActionResult<EmailAccountDto>> Create([FromBody] CreateEmailAccountRequest request, CancellationToken cancellationToken)
    {
        var result = await _emailAccountService.CreateAsync(request, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(new { message = result.Error });
        }

        return CreatedAtAction(nameof(GetById), new { id = result.Value!.Id }, result.Value);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<EmailAccountDto>> Update(Guid id, [FromBody] UpdateEmailAccountRequest request, CancellationToken cancellationToken)
    {
        var result = await _emailAccountService.UpdateAsync(id, request, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Email account not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }

        return Ok(result.Value);
    }

    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken cancellationToken)
    {
        var result = await _emailAccountService.SetActiveAsync(id, true, cancellationToken);
        return result.Succeeded ? NoContent() : NotFound(new { message = result.Error });
    }

    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken)
    {
        var result = await _emailAccountService.SetActiveAsync(id, false, cancellationToken);
        return result.Succeeded ? NoContent() : NotFound(new { message = result.Error });
    }

    [HttpPost("{id:guid}/test-connection")]
    public async Task<ActionResult<TestConnectionResult>> TestConnection(Guid id, CancellationToken cancellationToken)
    {
        var result = await _emailAccountService.TestConnectionAsync(id, cancellationToken);
        if (!result.Succeeded)
        {
            return NotFound(new { message = result.Error });
        }

        return Ok(result.Value);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _emailAccountService.DeleteAsync(id, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Error == "Email account not found." ? NotFound(new { message = result.Error }) : BadRequest(new { message = result.Error });
        }

        return NoContent();
    }
}

public record DetectMailSettingsRequest(string? EmailAddress, string? Password);
