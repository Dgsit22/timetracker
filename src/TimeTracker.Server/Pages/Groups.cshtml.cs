using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;

namespace TimeTracker.Server.Pages;

[Authorize(Policy = "AdminOnly")]
public class GroupsModel : PageModel
{
    private readonly TimeTrackerDbContext _db;

    public GroupsModel(TimeTrackerDbContext db)
    {
        _db = db;
    }

    public List<GroupSummary> Groups { get; private set; } = new();

    public List<string> KnownUsers { get; private set; } = new();

    [BindProperty]
    public CreateInputModel Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var group = new Group { GroupId = Guid.NewGuid(), Name = Input.Name };
        _db.Groups.Add(group);

        foreach (var member in (Input.Members ?? Array.Empty<string>()).Distinct())
        {
            _db.GroupMembers.Add(new GroupMember { GroupId = group.GroupId, UserName = member });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid groupId, CancellationToken cancellationToken)
    {
        // Also remove any exclusion rule targeting this group - otherwise it'd point at a
        // group that no longer exists.
        var rules = await _db.ExclusionRules.Where(r => r.GroupId == groupId).ToListAsync(cancellationToken);
        _db.ExclusionRules.RemoveRange(rules);

        var members = await _db.GroupMembers.Where(m => m.GroupId == groupId).ToListAsync(cancellationToken);
        _db.GroupMembers.RemoveRange(members);

        var group = await _db.Groups.FindAsync(new object[] { groupId }, cancellationToken);
        if (group is not null)
        {
            _db.Groups.Remove(group);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var groups = await _db.Groups.OrderBy(g => g.Name).ToListAsync(cancellationToken);
        var members = await _db.GroupMembers.ToListAsync(cancellationToken);

        Groups = groups
            .Select(g => new GroupSummary(
                g.GroupId,
                g.Name,
                members.Where(m => m.GroupId == g.GroupId).Select(m => m.UserName).OrderBy(u => u).ToList()))
            .ToList();

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
        [Required]
        public string Name { get; set; } = default!;

        public string[]? Members { get; set; }
    }
}

public record GroupSummary(Guid GroupId, string Name, List<string> Members);
