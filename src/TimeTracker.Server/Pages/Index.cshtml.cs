using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TimeTracker.Server.Pages;

public class IndexModel : PageModel
{
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(ILogger<IndexModel> logger)
    {
        _logger = logger;
    }

    public IActionResult OnGet()
    {
        // Authenticated users get the real landing page (Dashboard); this scaffold "Welcome"
        // page only still makes sense for anonymous visitors.
        return User.Identity?.IsAuthenticated == true ? RedirectToPage("/Dashboard") : Page();
    }
}
