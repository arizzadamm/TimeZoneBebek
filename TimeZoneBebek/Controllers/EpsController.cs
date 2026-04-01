using Microsoft.AspNetCore.Mvc;
using TimeZoneBebek.Services;

namespace TimeZoneBebek.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EpsController : ControllerBase
    {
        private readonly ElasticEpsService _epsService;

        public EpsController(ElasticEpsService epsService)
        {
            _epsService = epsService;
        }

        [HttpGet]
        public async Task<IActionResult> GetCurrentEps()
        {
            var snapshot = await _epsService.GetCurrentEpsAsync();
            return Ok(new
            {
                eventsPerSecond = snapshot.EventsPerSecond,
                eventsLastMinute = snapshot.EventsLastMinute,
                capturedAtUtc = snapshot.CapturedAtUtc
            });
        }
    }
}
