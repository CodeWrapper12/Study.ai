using System.Security.Claims;
using Accelerator.Api.Common;
using Accelerator.Api.Data;
using Accelerator.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Accelerator.Api.Features;

public static class CommunityEndpoints
{
    public record ThreadCreate(string Title, string Body);
    public record ReplyCreate(string Body);
    public record ReviewUpsert(int Rating, string Body);

    public static void MapCommunityEndpoints(this WebApplication app)
    {
        // ----- Q&A per lesson item -----
        app.MapGet("/items/{itemId:guid}/threads", async (Guid itemId, AppDbContext db) =>
        {
            var threads = await db.Threads
                .Where(t => t.ItemId == itemId)
                .OrderByDescending(t => t.CreatedAt)
                .Join(db.Users, t => t.UserId, u => u.Id, (t, u) => new
                {
                    t.Id, t.Title, t.Body, t.IsResolved, t.CreatedAt,
                    author = u.DisplayName,
                    replyCount = t.Replies.Count
                })
                .Take(100)
                .ToListAsync();
            return Results.Ok(threads);
        });

        app.MapGet("/threads/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var thread = await db.Threads
                .Where(t => t.Id == id)
                .Join(db.Users, t => t.UserId, u => u.Id, (t, u) => new { t, author = u.DisplayName })
                .FirstOrDefaultAsync();
            if (thread is null) return Results.NotFound();

            var replies = await db.Replies
                .Where(r => r.ThreadId == id)
                .OrderBy(r => r.CreatedAt)
                .Join(db.Users, r => r.UserId, u => u.Id, (r, u) => new
                {
                    r.Id, r.Body, r.IsInstructorReply, r.CreatedAt, author = u.DisplayName
                })
                .ToListAsync();

            return Results.Ok(new
            {
                thread.t.Id, thread.t.Title, thread.t.Body, thread.t.IsResolved,
                thread.t.CreatedAt, thread.author, replies
            });
        });

        app.MapPost("/items/{itemId:guid}/threads",
            async (Guid itemId, ThreadCreate req, ClaimsPrincipal principal, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "A question title is required." });
            if (!await db.Items.AnyAsync(i => i.Id == itemId)) return Results.NotFound();

            var thread = new DiscussionThread
            {
                ItemId = itemId, UserId = CurrentUser.Id(principal),
                Title = req.Title.Trim(), Body = req.Body?.Trim() ?? ""
            };
            db.Threads.Add(thread);
            await db.SaveChangesAsync();
            return Results.Ok(new { thread.Id });
        }).RequireAuthorization();

        app.MapPost("/threads/{id:guid}/replies",
            async (Guid id, ReplyCreate req, ClaimsPrincipal principal, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Body))
                return Results.BadRequest(new { error = "Reply cannot be empty." });
            if (!await db.Threads.AnyAsync(t => t.Id == id)) return Results.NotFound();

            db.Replies.Add(new DiscussionReply
            {
                ThreadId = id, UserId = CurrentUser.Id(principal),
                Body = req.Body.Trim(),
                IsInstructorReply = CurrentUser.IsStaff(principal)
            });
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization();

        app.MapPost("/threads/{id:guid}/resolve", async (Guid id, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var thread = await db.Threads.FindAsync(id);
            if (thread is null) return Results.NotFound();
            if (thread.UserId != CurrentUser.Id(principal) && !CurrentUser.IsStaff(principal))
                return Results.Forbid();
            thread.IsResolved = true;
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization();

        // ----- Reviews -----
        app.MapGet("/courses/{courseId:guid}/reviews", async (Guid courseId, AppDbContext db, int page = 1) =>
        {
            const int pageSize = 10;
            page = Math.Max(1, page);
            var total = await db.Reviews.CountAsync(r => r.CourseId == courseId);
            var items = await db.Reviews
                .Where(r => r.CourseId == courseId)
                .OrderByDescending(r => r.CreatedAt)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Join(db.Users, r => r.UserId, u => u.Id, (r, u) => new
                {
                    r.Rating, r.Body, r.CreatedAt, author = u.DisplayName, headline = u.Headline
                })
                .ToListAsync();
            return Results.Ok(new { total, page, pageSize, items });
        });

        app.MapPut("/courses/{courseId:guid}/reviews",
            async (Guid courseId, ReviewUpsert req, ClaimsPrincipal principal, AppDbContext db) =>
        {
            if (req.Rating is < 1 or > 5)
                return Results.BadRequest(new { error = "Rating must be between 1 and 5." });

            var userId = CurrentUser.Id(principal);
            if (!await db.Enrollments.AnyAsync(e => e.UserId == userId && e.CourseId == courseId))
                return Results.BadRequest(new { error = "Enroll in the course before reviewing it." });

            var review = await db.Reviews.FirstOrDefaultAsync(r => r.CourseId == courseId && r.UserId == userId);
            if (review is null)
            {
                review = new Review { CourseId = courseId, UserId = userId };
                db.Reviews.Add(review);
            }
            review.Rating = req.Rating;
            review.Body = req.Body?.Trim() ?? "";
            review.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization();
    }
}
