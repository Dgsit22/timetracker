using Microsoft.AspNetCore.Identity;

namespace TimeTracker.Server.Data;

public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = default!;
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Viewer = "Viewer";

    public static readonly string[] All = [Admin, Viewer];
}
