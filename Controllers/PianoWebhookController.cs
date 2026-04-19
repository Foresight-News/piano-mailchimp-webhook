using Microsoft.AspNetCore.Mvc;
using piano_mailchimp_webhook.Models;
using piano_mailchimp_webhook.Services;

namespace piano_mailchimp_webhook.Controllers;

[ApiController]
[Route("webhooks/piano")]
public sealed class PianoWebhookController(
    IPianoWebhookProcessor webhookProcessor,
    ILogger<PianoWebhookController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Receive([FromBody] PianoWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Received Piano webhook event {EventName} for uid {Uid}.",
            webhookEvent.Event ?? "unknown",
            webhookEvent.Uid ?? "unknown");

        await webhookProcessor.ProcessAsync(webhookEvent, cancellationToken);

        return Ok(new
        {
            success = true
        });
    }
}
