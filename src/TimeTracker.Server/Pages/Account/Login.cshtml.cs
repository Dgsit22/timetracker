using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TimeTracker.Server.Data;

namespace TimeTracker.Server.Pages.Account;

public class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;

    public LoginModel(SignInManager<ApplicationUser> signInManager)
    {
        _signInManager = signInManager;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        returnUrl ??= Url.Content("~/Activity");

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _signInManager.PasswordSignInAsync(
            Input.Email, Input.Password, isPersistent: true, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            return LocalRedirect(returnUrl);
        }

        // Lockout is on (5 failures, 5 minutes by Identity's defaults), and without its own
        // message a locked-out account keeps being told its correct password is wrong.
        if (result.IsLockedOut)
        {
            ModelState.AddModelError(
                string.Empty,
                "Too many failed attempts. Wait a few minutes, then try again.");
            return Page();
        }

        // Deliberately does not say which of the two was wrong - that would confirm whether an
        // account exists to anyone who can reach the login page.
        ModelState.AddModelError(
            string.Empty,
            "That email and password do not match. Check both and try again.");
        return Page();
    }

    public class InputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = default!;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = default!;
    }
}
