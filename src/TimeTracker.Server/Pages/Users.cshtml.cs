using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TimeTracker.Server.Data;

namespace TimeTracker.Server.Pages;

[Authorize(Policy = "AdminOnly")]
public class UsersModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;

    public UsersModel(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    public List<(ApplicationUser User, IList<string> Roles)> Users { get; private set; } = new();

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadUsersAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadUsersAsync();
            return Page();
        }

        var user = new ApplicationUser
        {
            UserName = Input.Email,
            Email = Input.Email,
            DisplayName = Input.DisplayName,
            EmailConfirmed = true,
        };

        var result = await _userManager.CreateAsync(user, Input.Password);
        if (result.Succeeded)
        {
            await _userManager.AddToRoleAsync(user, Input.Role);
        }
        else
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            await LoadUsersAsync();
            return Page();
        }

        return RedirectToPage();
    }

    /// <summary>
    /// Admin-set password. There is no mail server configured, so the usual emailed reset link
    /// is not an option - without this, the only way to recover a forgotten password was to
    /// delete the account and recreate it, which loses its roles with it.
    ///
    /// Goes through a reset token rather than a direct hash write so Identity applies its own
    /// password rules and stamps the security stamp, which invalidates the user's existing
    /// sessions the way a real password change should.
    /// </summary>
    public async Task<IActionResult> OnPostSetPasswordAsync(string userId, string newPassword)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            StatusMessage = "That user no longer exists.";
            return RedirectToPage();
        }

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            StatusMessage = "Enter a new password before saving.";
            return RedirectToPage();
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, newPassword);

        StatusMessage = result.Succeeded
            ? $"Password updated for {user.Email}. They will need to sign in again."
            : string.Join(" ", result.Errors.Select(e => e.Description));

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is not null)
        {
            await _userManager.DeleteAsync(user);
        }

        return RedirectToPage();
    }

    private async Task LoadUsersAsync()
    {
        var users = new List<(ApplicationUser, IList<string>)>();
        foreach (var user in _userManager.Users.ToList())
        {
            var roles = await _userManager.GetRolesAsync(user);
            users.Add((user, roles));
        }

        Users = users;
    }

    public class InputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = default!;

        [Required]
        public string DisplayName { get; set; } = default!;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = default!;

        [Required]
        public string Role { get; set; } = Roles.Viewer;
    }
}
