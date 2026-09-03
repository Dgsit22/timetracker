using Microsoft.AspNetCore.Identity;

namespace TimeTracker.Server.Data;

/// <summary>
/// Ensures the Admin/Viewer roles exist and, if no users exist yet, creates the
/// first Admin from configuration so there's always a way into the console.
/// </summary>
public static class IdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IdentitySeeder).FullName!);

        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }

        if (userManager.Users.Any())
        {
            return;
        }

        var adminEmail = configuration["Admin:Email"];
        var adminPassword = configuration["Admin:Password"];
        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            logger.LogWarning("No users exist and Admin:Email/Admin:Password are not configured - no admin account was created. There is no way to log in until one is added.");
            return;
        }

        var admin = new ApplicationUser
        {
            UserName = adminEmail,
            Email = adminEmail,
            DisplayName = "Administrator",
            EmailConfirmed = true,
        };

        var result = await userManager.CreateAsync(admin, adminPassword);
        if (result.Succeeded)
        {
            await userManager.AddToRoleAsync(admin, Roles.Admin);
            logger.LogInformation("Seeded the first Admin account for {Email}.", adminEmail);
        }
        else
        {
            logger.LogError(
                "Failed to create the seeded Admin account for {Email}: {Errors}. No admin account exists - fix Admin:Password (or Admin:Email) and restart the server.",
                adminEmail,
                string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }
}
