using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TimeTracker.Server.Data;

namespace TimeTracker.Server.Pages.Account;

/// <summary>
/// Lets a signed-in user change their own password. Until this existed the seeded admin password
/// could not be changed at all from the console - ADMIN_PASSWORD in .env is only read when the
/// database has no users, so editing it there changes nothing, and the only workaround was to
/// create a second admin and delete the first.
/// </summary>
[Authorize]
public class ChangePasswordModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public ChangePasswordModel(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            // The cookie outlived the account it names.
            return RedirectToPage("/Account/Login");
        }

        var result = await _userManager.ChangePasswordAsync(user, Input.CurrentPassword, Input.NewPassword);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        // Changing the password rotates the security stamp, which invalidates this session's
        // cookie along with every other one. Re-signing in keeps the person who just made the
        // change logged in, while sessions elsewhere still end.
        await _signInManager.RefreshSignInAsync(user);

        StatusMessage = "Your password has been changed.";
        return RedirectToPage();
    }

    public class InputModel
    {
        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = default!;

        [Required]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "The new password must be at least 6 characters.")]
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string NewPassword { get; set; } = default!;

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm new password")]
        [Compare(nameof(NewPassword), ErrorMessage = "The two new passwords do not match.")]
        public string ConfirmPassword { get; set; } = default!;
    }
}
