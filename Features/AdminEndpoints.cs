using System.Security.Claims;
using System.Text.Json;
using Accelerator.Api.Common;
using Accelerator.Api.Data;
using Accelerator.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Accelerator.Api.Features;

public static class AdminEndpoints
{
    public record CourseUpsert(string Slug, string Title, string Subtitle, string DescriptionMarkdown,
        string Category, string Level, string Language, string? ThumbnailUrl, string? PromoVideoUrl,
        int WeeksEstimate, List<string> Outcomes, List<string> Prerequisites, List<string> TargetAudience);
    public record SectionUpsert(string Code, string Title, string Summary, string Track, string WeekRange, int Order);
    public record ItemUpsert(string Type, string Title, int EstMinutes, int Order,
        bool IsFreePreview, bool CountsTowardCompletion, JsonElement Payload);
    public record ReorderRequest(List<Guid> OrderedIds);
    public record GradeRequest(int GradePercent, string Feedback, bool RequestChanges);

    public static void MapAdminEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/admin").RequireAuthorization("Staff");

        // ---------- Courses ----------
        g.MapGet("/courses", async (AppDbContext db) =>
            Results.Ok(await db.Courses
                .OrderByDescending(c => c.UpdatedAt)
                .Select(c => new
                {
                    c.Id, c.Slug, c.Title, c.Subtitle, status = c.Status.ToString(),
                    level = c.Level.ToString(), c.Category, c.UpdatedAt,
                    sections = c.Sections.Count,
                    items = c.Sections.SelectMany(s => s.Items).Count()
                })
                .ToListAsync()));

        g.MapGet("/courses/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var c = await db.Courses
                .Include(x => x.Sections.OrderBy(s => s.Order))
                .ThenInclude(s => s.Items.OrderBy(i => i.Order))
                .FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return Results.NotFound();
            return Results.Ok(new
            {
                c.Id, c.Slug, c.Title, c.Subtitle, c.DescriptionMarkdown, c.Category,
                level = c.Level.ToString(), c.Language, c.ThumbnailUrl, c.PromoVideoUrl,
                c.WeeksEstimate, status = c.Status.ToString(),
                outcomes = Payloads.Read<List<string>>(c.OutcomesJson),
                prerequisites = Payloads.Read<List<string>>(c.PrerequisitesJson),
                targetAudience = Payloads.Read<List<string>>(c.TargetAudienceJson),
                sections = c.Sections.Select(s => new
                {
                    s.Id, s.Code, s.Title, s.Summary, s.Track, s.WeekRange, s.Order,
                    items = s.Items.Select(i => new
                    {
                        i.Id, i.Title, type = i.Type.ToString(), i.EstMinutes, i.Order,
                        i.IsFreePreview, i.CountsTowardCompletion
                    })
                })
            });
        });

        g.MapPost("/courses", async (CourseUpsert req, ClaimsPrincipal principal, AppDbContext db) =>
        {
            if (await db.Courses.AnyAsync(c => c.Slug == req.Slug))
                return Results.Conflict(new { error = "A course with this slug already exists." });

            var course = Apply(new Course { InstructorId = CurrentUser.Id(principal) }, req);
            db.Courses.Add(course);
            await db.SaveChangesAsync();
            return Results.Ok(new { course.Id });
        });

        g.MapPut("/courses/{id:guid}", async (Guid id, CourseUpsert req, AppDbContext db) =>
        {
            var course = await db.Courses.FindAsync(id);
            if (course is null) return Results.NotFound();
            Apply(course, req);
            course.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapPost("/courses/{id:guid}/publish", async (Guid id, AppDbContext db) =>
        {
            var course = await db.Courses.Include(c => c.Sections).ThenInclude(s => s.Items)
                .FirstOrDefaultAsync(c => c.Id == id);
            if (course is null) return Results.NotFound();
            if (!course.Sections.Any(s => s.Items.Count > 0))
                return Results.BadRequest(new { error = "Add at least one section with content before publishing." });
            course.Status = CourseStatus.Published;
            course.PublishedAt ??= DateTime.UtcNow;
            course.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapPost("/courses/{id:guid}/unpublish", async (Guid id, AppDbContext db) =>
        {
            var course = await db.Courses.FindAsync(id);
            if (course is null) return Results.NotFound();
            course.Status = CourseStatus.Draft;
            course.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapDelete("/courses/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var course = await db.Courses.FindAsync(id);
            if (course is null) return Results.NotFound();
            db.Courses.Remove(course);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ---------- Sections ----------
        g.MapPost("/courses/{courseId:guid}/sections", async (Guid courseId, SectionUpsert req, AppDbContext db) =>
        {
            if (!await db.Courses.AnyAsync(c => c.Id == courseId)) return Results.NotFound();
            var section = new Section
            {
                CourseId = courseId, Code = req.Code, Title = req.Title,
                Summary = req.Summary, Track = req.Track, WeekRange = req.WeekRange, Order = req.Order
            };
            db.Sections.Add(section);
            await db.SaveChangesAsync();
            return Results.Ok(new { section.Id });
        });

        g.MapPut("/sections/{id:guid}", async (Guid id, SectionUpsert req, AppDbContext db) =>
        {
            var s = await db.Sections.FindAsync(id);
            if (s is null) return Results.NotFound();
            (s.Code, s.Title, s.Summary, s.Track, s.WeekRange, s.Order)
                = (req.Code, req.Title, req.Summary, req.Track, req.WeekRange, req.Order);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapDelete("/sections/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var s = await db.Sections.FindAsync(id);
            if (s is null) return Results.NotFound();
            db.Sections.Remove(s);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapPost("/courses/{courseId:guid}/sections/reorder",
            async (Guid courseId, ReorderRequest req, AppDbContext db) =>
        {
            var sections = await db.Sections.Where(s => s.CourseId == courseId).ToListAsync();
            for (var i = 0; i < req.OrderedIds.Count; i++)
            {
                var s = sections.FirstOrDefault(x => x.Id == req.OrderedIds[i]);
                if (s is not null) s.Order = i + 1;
            }
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ---------- Items ----------
        g.MapGet("/items/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var item = await db.Items.FindAsync(id);
            if (item is null) return Results.NotFound();
            return Results.Ok(new
            {
                item.Id, item.SectionId, item.Title, type = item.Type.ToString(),
                item.EstMinutes, item.Order, item.IsFreePreview, item.CountsTowardCompletion,
                payload = JsonSerializer.Deserialize<JsonElement>(item.PayloadJson)
            });
        });

        g.MapPost("/sections/{sectionId:guid}/items", async (Guid sectionId, ItemUpsert req, AppDbContext db) =>
        {
            if (!await db.Sections.AnyAsync(s => s.Id == sectionId)) return Results.NotFound();
            if (!Enum.TryParse<ItemType>(req.Type, true, out var type))
                return Results.BadRequest(new { error = $"Unknown item type '{req.Type}'." });
            var err = ValidatePayload(type, req.Payload);
            if (err is not null) return Results.BadRequest(new { error = err });

            var item = new LessonItem
            {
                SectionId = sectionId, Type = type, Title = req.Title, EstMinutes = req.EstMinutes,
                Order = req.Order, IsFreePreview = req.IsFreePreview,
                CountsTowardCompletion = req.CountsTowardCompletion,
                PayloadJson = req.Payload.GetRawText()
            };
            db.Items.Add(item);
            await db.SaveChangesAsync();
            return Results.Ok(new { item.Id });
        });

        g.MapPut("/items/{id:guid}", async (Guid id, ItemUpsert req, AppDbContext db) =>
        {
            var item = await db.Items.FindAsync(id);
            if (item is null) return Results.NotFound();
            if (!Enum.TryParse<ItemType>(req.Type, true, out var type))
                return Results.BadRequest(new { error = $"Unknown item type '{req.Type}'." });
            var err = ValidatePayload(type, req.Payload);
            if (err is not null) return Results.BadRequest(new { error = err });

            (item.Type, item.Title, item.EstMinutes, item.Order, item.IsFreePreview, item.CountsTowardCompletion)
                = (type, req.Title, req.EstMinutes, req.Order, req.IsFreePreview, req.CountsTowardCompletion);
            item.PayloadJson = req.Payload.GetRawText();
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapDelete("/items/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var item = await db.Items.FindAsync(id);
            if (item is null) return Results.NotFound();
            db.Items.Remove(item);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapPost("/sections/{sectionId:guid}/items/reorder",
            async (Guid sectionId, ReorderRequest req, AppDbContext db) =>
        {
            var items = await db.Items.Where(i => i.SectionId == sectionId).ToListAsync();
            for (var i = 0; i < req.OrderedIds.Count; i++)
            {
                var item = items.FirstOrDefault(x => x.Id == req.OrderedIds[i]);
                if (item is not null) item.Order = i + 1;
            }
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ---------- Grading ----------
        g.MapGet("/submissions", async (AppDbContext db, string status = "Submitted") =>
        {
            Enum.TryParse<SubmissionStatus>(status, true, out var st);
            return Results.Ok(await db.Submissions
                .Where(s => s.Status == st)
                .OrderBy(s => s.SubmittedAt)
                .Join(db.Users, s => s.UserId, u => u.Id, (s, u) => new { s, student = u.DisplayName })
                .Join(db.Items, x => x.s.ItemId, i => i.Id, (x, i) => new
                {
                    x.s.Id, x.student, itemTitle = i.Title,
                    x.s.ContentText, x.s.ArtifactUrl, x.s.SubmittedAt
                })
                .Take(100)
                .ToListAsync());
        });

        g.MapPost("/submissions/{id:guid}/grade", async (Guid id, GradeRequest req, AppDbContext db) =>
        {
            var sub = await db.Submissions.FindAsync(id);
            if (sub is null) return Results.NotFound();
            sub.GradePercent = Math.Clamp(req.GradePercent, 0, 100);
            sub.Feedback = req.Feedback;
            sub.Status = req.RequestChanges ? SubmissionStatus.ChangesRequested : SubmissionStatus.Graded;
            sub.GradedAt = DateTime.UtcNow;

            if (!req.RequestChanges && sub.GradePercent >= 60)
            {
                var prog = await db.Progress.FirstOrDefaultAsync(p =>
                    p.UserId == sub.UserId && p.ItemId == sub.ItemId)
                    ?? db.Progress.Add(new ItemProgress { UserId = sub.UserId, ItemId = sub.ItemId }).Entity;
                if (!prog.Completed)
                {
                    prog.Completed = true;
                    prog.CompletedAt = DateTime.UtcNow;
                    var user = await db.Users.FindAsync(sub.UserId);
                    user!.Xp += 75;
                }
                prog.BestScorePercent = Math.Max(prog.BestScorePercent ?? 0, sub.GradePercent.Value);
            }
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ---------- Users ----------
        g.MapGet("/users", async (AppDbContext db) =>
            Results.Ok(await db.Users
                .OrderByDescending(u => u.CreatedAt)
                .Select(u => new { u.Id, u.Email, u.DisplayName, u.Role, u.Xp, u.CreatedAt })
                .Take(200).ToListAsync()));

        g.MapPost("/users/{id:guid}/role", async (Guid id, RoleRequest req, AppDbContext db) =>
        {
            if (req.Role is not ("Student" or "Instructor" or "Admin"))
                return Results.BadRequest(new { error = "Role must be Student, Instructor or Admin." });
            var user = await db.Users.FindAsync(id);
            if (user is null) return Results.NotFound();
            user.Role = req.Role;
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization("Admin");
    }

    public record RoleRequest(string Role);

    private static Course Apply(Course c, CourseUpsert req)
    {
        c.Slug = req.Slug.Trim().ToLowerInvariant();
        c.Title = req.Title;
        c.Subtitle = req.Subtitle;
        c.DescriptionMarkdown = req.DescriptionMarkdown;
        c.Category = req.Category;
        c.Level = Enum.TryParse<CourseLevel>(req.Level, true, out var lv) ? lv : CourseLevel.Advanced;
        c.Language = req.Language;
        c.ThumbnailUrl = req.ThumbnailUrl;
        c.PromoVideoUrl = req.PromoVideoUrl;
        c.WeeksEstimate = req.WeeksEstimate;
        c.OutcomesJson = Payloads.Write(req.Outcomes);
        c.PrerequisitesJson = Payloads.Write(req.Prerequisites);
        c.TargetAudienceJson = Payloads.Write(req.TargetAudience);
        return c;
    }

    private static string? ValidatePayload(ItemType type, JsonElement payload)
    {
        try
        {
            var raw = payload.GetRawText();
            return type switch
            {
                ItemType.Article when Payloads.Read<ArticlePayload>(raw) is null or { ContentMarkdown: null }
                    => "Article payload requires contentMarkdown.",
                ItemType.Video when Payloads.Read<VideoPayload>(raw) is null or { Url: null }
                    => "Video payload requires url.",
                ItemType.Quiz when (Payloads.Read<QuizPayload>(raw)?.Questions?.Count ?? 0) == 0
                    => "Quiz payload requires at least one question.",
                ItemType.Assignment when Payloads.Read<AssignmentPayload>(raw) is null or { InstructionsMarkdown: null }
                    => "Assignment payload requires instructionsMarkdown.",
                ItemType.CodeExercise when Payloads.Read<CodeExercisePayload>(raw) is null or { StarterCode: null }
                    => "Code exercise payload requires starterCode.",
                _ => null
            };
        }
        catch (JsonException)
        {
            return "Payload is not valid JSON for this item type.";
        }
    }
}
