using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DashBoard.Pages;

public class IndexModel : PageModel
{
	public int? UserId { get; private set; }

	public IActionResult OnGet(int? userId)
	{
		if (!userId.HasValue || userId.Value <= 0)
			return BadRequest("A valid userId query parameter is required.");

		UserId = userId.Value;
		return Page();
	}
}