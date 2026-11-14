using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TopupLedger.Infrastructure.Data;

namespace TopupLedger.Api.Controllers;

/// <summary>
/// GET /health - checks MySQL connectivity. Returns 200 with status:ok if the DB is reachable.
/// </summary>
[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    private readonly TopupLedgerDbContext _db;
    public HealthController(TopupLedgerDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        try
        {
            var canConnect = await _db.Database.CanConnectAsync();
            if (!canConnect)
                return StatusCode(503, new { status = "unhealthy", database = "unreachable" });
            return Ok(new { status = "ok", database = "ok", timestamp = DateTime.UtcNow });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { status = "unhealthy", error = ex.Message });
        }
    }
}
