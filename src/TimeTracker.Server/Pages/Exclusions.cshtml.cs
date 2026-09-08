using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;

namespace TimeTracker.Server.Pages;

[Authorize(Policy = "AdminOnly")]
public class ExclusionsModel : PageModel
{
    private readonly TimeTrackerDbContext _db;

    public ExclusionsModel(TimeTrackerDbContext db)
    {
        _db = db;
    }

    public List<ExclusionSummary> Rules { get; private set; } = new();

    public List<string> KnownUsers { get; private set; } = new();

    public List<Group> KnownGroups { get; private set; } = new();

    [BindProperty]
    public CreateInputModel Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (Input.TargetType == "User" && !string.IsNullOrWhiteSpace(Input.UserName))
        {
            _db.ExclusionRules.Add(new ExclusionRule
            {
                ExclusionRuleId = Guid.NewGuid(),
                UserName = Input.UserName,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
        }
        else if (Input.TargetType == "Group" && Input.GroupId is { } groupId)
        {
            _db.ExclusionRules.Add(new ExclusionRule
            {
                ExclusionRuleId = Guid.NewGuid(),
                GroupId = groupId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            ModelState.AddModelError(string.Empty, "Choose a user or a group to exclude.");
            await LoadAsync(cancellationToken);
            return Page();
        }

        await _db.SaveChangesAsync(cancellationToken);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid exclusionRuleId, CancellationToken cancellationToken)
    {
        var rule = await _db.ExclusionRules.FindAsync(new object[] { exclusionRuleId }, cancellationToken);
        if (rule is not null)
        {
            _db.ExclusionRules.Remove(rule);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var rules = await _db.ExclusionRules.OrderByDescending(r => r.CreatedAtUtc).ToListAsync(cancellationToken);
        var groups = await _db.Groups.ToListAsync(cancellationToken);
        var groupNameById = groups.ToDictionary(g => g.GroupId, g => g.Name);

        Rules = rules
            .Select(r => new ExclusionSummary(
                r.ExclusionRuleId,
                r.UserName is not null ? "User" : "Group",
                r.UserName ?? (r.GroupId is { } gid ? groupNameById.GetValueOrDefault(gid, "(deleted group)") : "(unknown)"),
                r.CreatedAtUtc))
            .ToList();

        KnownGroups = groups.OrderBy(g => g.Name).ToList();

        KnownUsers = await _db.AppUsageEvents.Select(e => e.UserName)
            .Union(_db.IdlePeriods.Select(e => e.UserName))
            .Union(_db.UrlVisits.Select(e => e.UserName))
            .Union(_db.SessionBreaks.Select(e => e.UserName))
            .Union(_db.Screenshots.Select(e => e.UserName))
            .Distinct()
            .OrderBy(u => u)
            .ToListAsync(cancellationToken);
    }

    public class CreateInputModel
    {
        public string TargetType { get; set; } = "User";

        public string? UserName { get; set; }

        public Guid? GroupId { get; set; }
    }
}

public record ExclusionSummary(Guid ExclusionRuleId, string TargetType, string TargetLabel, DateTimeOffset CreatedAtUtc);
