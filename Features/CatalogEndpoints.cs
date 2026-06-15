using System.Security.Claims;
using Accelerator.Api.Common;
using Accelerator.Api.Data;
using Accelerator.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Accelerator.Api.Features;

public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/catalog");

        // GET /catalog/courses?search=&category=&level=&page=&pageSize=
        g.MapGet("/courses", async (AppDbContext db, string? search, string? category,
            string? level, int page = 1, int pageSize = 12) =>
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 50);

            var q = db.Courses.Where(c => c.Status == CourseStatus.Published);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = $"%{search.Trim()}%";
                q = q.Where(c => EF.Functions.ILike(c.Title, s) || EF.Functions.ILike(c.Subtitle, s));
            }
            if (!string.IsNullOrWhiteSpace(category))
                q = q.Where(c => c.Category == category);
            if (Enum.TryParse<CourseLevel>(level, true, out var lv))
                q = q.Where(c => c.Level == lv);

            var total = await q.CountAsync();
            var items = await q
                .OrderByDescending(c => c.PublishedAt)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(c => new
                {
                    c.Id, c.Slug, c.Title, c.Subtitle, c.Category,
                    level = c.Level.ToString(), c.ThumbnailUrl, c.WeeksEstimate,
                    instructor = c.Instructor.DisplayName,
                    rating = c.Reviews.Any() ? Math.Round(c.Reviews.Average(r => r.Rating), 1) : (double?)null,
                    reviewCount = c.Reviews.Count,
                    itemCount = c.Sections.SelectMany(s => s.Items).Count(i => i.CountsTowardCompletion)
                })
                .ToListAsync();

            return Results.Ok(new { total, page, pageSize, items });
        });

        g.MapGet("/categories", async (AppDbContext db) =>
            Results.Ok(await db.Courses
                .Where(c => c.Status == CourseStatus.Published)
                .Select(c => c.Category).Distinct().OrderBy(c => c).ToListAsync()));

        // Rich course landing page payload
        g.MapGet("/courses/{slug}", async (string slug, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var course = await db.Courses
                .Include(c => c.Instructor)
                .Include(c => c.Sections.OrderBy(s => s.Order))
                .ThenInclude(s => s.Items.OrderBy(i => i.Order))
                .FirstOrDefaultAsync(c => c.Slug == slug && c.Status == CourseStatus.Published);
            if (course is null) return Results.NotFound();

            var userId = CurrentUser.IdOrNull(principal);
            var enrolled = userId.HasValue &&
                await db.Enrollments.AnyAsync(e => e.UserId == userId && e.CourseId == course.Id);

            var ratings = await db.Reviews.Where(r => r.CourseId == course.Id)
                .GroupBy(r => r.Rating)
                .Select(gp => new { rating = gp.Key, count = gp.Count() })
                .ToListAsync();
            var reviewCount = ratings.Sum(r => r.count);
            var avg = reviewCount == 0 ? (double?)null :
                Math.Round((double)ratings.Sum(r => r.rating * r.count) / reviewCount, 1);

            var latestReviews = await db.Reviews
                .Where(r => r.CourseId == course.Id)
                .OrderByDescending(r => r.CreatedAt).Take(6)
                .Join(db.Users, r => r.UserId, u => u.Id, (r, u) => new
                {
                    r.Rating, r.Body, r.CreatedAt, author = u.DisplayName, headline = u.Headline
                })
                .ToListAsync();

            var totalMinutes = course.Sections.SelectMany(s => s.Items).Sum(i => i.EstMinutes);

            return Results.Ok(new
            {
                course.Id, course.Slug, course.Title, course.Subtitle,
                descriptionMarkdown = course.DescriptionMarkdown,
                course.Category, level = course.Level.ToString(), course.Language,
                course.ThumbnailUrl, course.PromoVideoUrl, course.WeeksEstimate,
                totalHours = Math.Round(totalMinutes / 60.0, 1),
                outcomes = Payloads.Read<List<string>>(course.OutcomesJson),
                prerequisites = Payloads.Read<List<string>>(course.PrerequisitesJson),
                targetAudience = Payloads.Read<List<string>>(course.TargetAudienceJson),
                instructor = new
                {
                    course.Instructor.DisplayName,
                    course.Instructor.Headline
                },
                enrolled,
                rating = avg, reviewCount,
                ratingBreakdown = ratings,
                latestReviews,
                sections = course.Sections.Select(s => new
                {
                    s.Id, s.Code, s.Title, s.Summary, s.Track, s.WeekRange,
                    items = s.Items.Select(i => new
                    {
                        i.Id, i.Title, type = i.Type.ToString(), i.EstMinutes, i.IsFreePreview
                    })
                })
            });
        });
    }
}
