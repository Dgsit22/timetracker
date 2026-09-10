using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;
using TimeTracker.Server.Ingest;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddDbContext<TimeTrackerDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("TimeTracker")));

// Persist Data Protection keys across container restarts/redeploys - without
// this, every restart invalidates in-flight antiforgery tokens and login cookies.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("/app/keys"))
    .SetApplicationName("TimeTracker.Server");

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<TimeTrackerDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";

    // Always, not the SameAsRequest default: this cookie is the entire admin session, and the
    // default would happily send it over a plaintext connection if the app were ever reached over
    // one. HttpOnly keeps it out of reach of script (defence in depth alongside the screenshot
    // content-type fix). Lax rather than Strict because the login flow redirects back into the
    // site, which Strict would break.
    //
    // The escape hatch exists because Always makes login impossible over HTTP - the browser
    // discards the cookie, so the POST succeeds, the redirect happens, and the user lands back on
    // the login page forever with no error to explain it. Only docker-compose.local-http.yml sets
    // this, and it defaults to false, so a deployment that doesn't deliberately opt out stays
    // secure.
    var allowInsecureCookies = builder.Configuration.GetValue("Security:AllowInsecureCookies", false);
    options.Cookie.SecurePolicy = allowInsecureCookies
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(Roles.Admin));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<TimeTrackerDbContext>().Database.Migrate();
    await IdentitySeeder.SeedAsync(scope.ServiceProvider, app.Configuration);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapIngestEndpoints(app.Configuration);

app.Run();
