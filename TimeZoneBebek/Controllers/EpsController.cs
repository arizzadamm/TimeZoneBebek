using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
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
       
            int currentEps = await _epsService.GetCurrentEpsAsync();

  
            return Ok(new { eps = currentEps });
        }
    }
}