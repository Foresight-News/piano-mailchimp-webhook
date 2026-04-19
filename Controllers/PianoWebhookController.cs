using Microsoft.AspNetCore.Mvc;
using piano_mailchimp_webhook.Models;
using piano_mailchimp_webhook.Services;

namespace piano_mailchimp_webhook.Controllers;

[ApiController]
[Route("webhooks/piano")]
public sealed class PianoWebhookController(IPianoWebhookService webhookService) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Receive([FromBody] PianoWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        await webhookService.ProcessAsync(webhookEvent, cancellationToken);
        return Accepted();
    }
}
