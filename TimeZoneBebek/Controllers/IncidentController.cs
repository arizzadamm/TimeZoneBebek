using Microsoft.AspNetCore.Mvc;
using TimeZoneBebek.Models;
using TimeZoneBebek.Services;

namespace TimeZoneBebek.Controllers
{
    [ApiController]
    [Route("api/incidents")]
    public class IncidentController : ControllerBase
    {
        private readonly IncidentService _service;

        public IncidentController(IncidentService service)
        {
            _service = service;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var data = await _service.GetAllAsync();
            return Ok(data);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Incident newInc)
        {
            var result = await _service.AddAsync(newInc);
            if (!result.Success) return Conflict(new { message = result.Message });
            return Ok(new { message = result.Message, id = result.Id });
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string id, [FromBody] Incident updatedInc)
        {
            var success = await _service.UpdateAsync(id, updatedInc);
            if (!success) return NotFound(new { message = "Incident Not Found" });
            return Ok(new { message = "Incident Updated" });
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var success = await _service.DeleteAsync(id);
            if (!success) return NotFound(new { message = "Incident Not Found" });
            return Ok(new { message = "Incident Deleted" });
        }

        [HttpPut("{id}/status")]
        public async Task<IActionResult> UpdateStatus(string id, [FromBody] string newStatus)
        {
            if (!new[] { "OPEN", "RESOLVED", "INVESTIGATING" }.Contains(newStatus.ToUpper()))
                return BadRequest(new { message = "Invalid Status" });

            var success = await _service.UpdateStatusAsync(id, newStatus);
            if (!success) return NotFound(new { message = "Incident Not Found" });
            return Ok(new { message = "Status Updated" });
        }

        [HttpPut("bulk-status")]
        public async Task<IActionResult> BulkUpdateStatus([FromBody] BulkUpdateDto req)
        {
            var count = await _service.BulkUpdateStatusAsync(req);
            return Ok(new { message = $"{count} Incidents Updated" });
        }

        [HttpPost("analyze/{id}")]
        public async Task<IActionResult> Analyze(string id)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z0-9\-]+$"))
                return BadRequest(new { message = "Invalid ID" });

            var result = await _service.AnalyzeWithAiAsync(id);
            if (!result.Success) return BadRequest(new { message = result.Analysis });
            return Ok(new { analysis = result.Analysis });
        }
    }
}
