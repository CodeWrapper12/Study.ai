using System.Security.Claims;
using Accelerator.Api.Common;
using Accelerator.Api.Data;
using Accelerator.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Accelerator.Api.Features;

public static class MeEndpoints
{
    public record NoteUpsert(string Body);

    public static void MapMeEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/me").RequireAuthorization();

        g.MapGet("/dashboard", async (ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = CurrentUser.Id(principal);
            var user = await db.Users.FindAsync(userId);
            if (user is null) return Results.NotFound();

            // Streak from item completions
            var days = await db.Progress
                .Where(p => p.UserId == userId && p.Completed && p.CompletedAt != null)
                .Select(p => p.CompletedAt!.Value.Date)
                .Distinct().ToListAsync();
            days.Sort((a, b) => b.CompareTo(a));
            var streak = 0;
            var cursor = DateTime.UtcNow.Date;
            if (days.Count > 0 && days[0] == cursor.AddDays(-1)) cursor = cursor.AddDays(-1);
            foreach (var d in days)
            {
                if (d != cursor) break;
                streak++;
                cursor = cursor.AddDays(-1);
            }

            // Enrollments with progress + resume
            var enrollments = await db.Enrollments
                .Where(e => e.UserId == userId)
                .Join(db.Courses, e => e.CourseId, c => c.Id, (e, c) => new { e, c })
                .ToListAsync();

            var cards = new List<object>();
            foreach (var x in enrollments)
            {
                var itemIds = await db.Items
                    .Where(i => i.Section.CourseId == x.c.Id && i.CountsTowardCompletion)
                    .Select(i => i.Id).ToListAsync();
                var done = await db.Progress.CountAsync(p =>
                    p.UserId == userId && p.Completed && itemIds.Contains(p.ItemId));
                cards.Add(new
                {
                    x.c.Slug, x.c.Title, x.c.ThumbnailUrl,
                    total = itemIds.Count, done,
                    percent = itemIds.Count == 0 ? 0 : (int)Math.Round(100.0 * done / itemIds.Count),
                    resumeItemId = x.e.LastItemId
                });
            }

            return Results.Ok(new
            {
                user.DisplayName, user.Xp,
                level = user.Xp / LearningEndpoints.XpPerLevel + 1,
                xpIntoLevel = user.Xp % LearningEndpoints.XpPerLevel,
                xpPerLevel = LearningEndpoints.XpPerLevel,
                streak,
                certificates = await db.Certificates.CountAsync(c => c.UserId == userId),
                courses = cards
            });
        });

        // ----- Notes -----
        g.MapGet("/notes", async (ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = CurrentUser.Id(principal);
            var notes = await db.Notes
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.UpdatedAt)
                .Join(db.Items, n => n.ItemId, i => i.Id, (n, i) => new
                {
                    n.Id, n.Body, n.UpdatedAt, n.ItemId, itemTitle = i.Title
                })
                .ToListAsync();
            return Results.Ok(notes);
        });

        g.MapGet("/items/{itemId:guid}/notes", async (Guid itemId, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = CurrentUser.Id(principal);
            return Results.Ok(await db.Notes
                .Where(n => n.UserId == userId && n.ItemId == itemId)
                .OrderByDescending(n => n.CreatedAt)
                .Select(n => new { n.Id, n.Body, n.UpdatedAt })
                .ToListAsync());
        });

        g.MapPost("/items/{itemId:guid}/notes",
            async (Guid itemId, NoteUpsert req, ClaimsPrincipal principal, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Body))
                return Results.BadRequest(new { error = "Note cannot be empty." });
            var note = new Note { UserId = CurrentUser.Id(principal), ItemId = itemId, Body = req.Body.Trim() };
            db.Notes.Add(note);
            await db.SaveChangesAsync();
            return Results.Ok(new { note.Id });
        });

        g.MapPut("/notes/{id:guid}", async (Guid id, NoteUpsert req, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var note = await db.Notes.FirstOrDefaultAsync(n =>
                n.Id == id && n.UserId == CurrentUser.Id(principal));
            if (note is null) return Results.NotFound();
            note.Body = req.Body.Trim();
            note.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapDelete("/notes/{id:guid}", async (Guid id, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var note = await db.Notes.FirstOrDefaultAsync(n =>
                n.Id == id && n.UserId == CurrentUser.Id(principal));
            if (note is null) return Results.NotFound();
            db.Notes.Remove(note);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ----- Bookmarks -----
        g.MapPost("/items/{itemId:guid}/bookmark", async (Guid itemId, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = CurrentUser.Id(principal);
            var existing = await db.Bookmarks.FirstOrDefaultAsync(b => b.UserId == userId && b.ItemId == itemId);
            if (existing is null)
            {
                db.Bookmarks.Add(new Bookmark { UserId = userId, ItemId = itemId });
                await db.SaveChangesAsync();
                return Results.Ok(new { bookmarked = true });
            }
            db.Bookmarks.Remove(existing);
            await db.SaveChangesAsync();
            return Results.Ok(new { bookmarked = false });
        });

        g.MapGet("/bookmarks", async (ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = CurrentUser.Id(principal);
            return Results.Ok(await db.Bookmarks
                .Where(b => b.UserId == userId)
                .OrderByDescending(b => b.CreatedAt)
                .Join(db.Items, b => b.ItemId, i => i.Id, (b, i) => new
                {
                    b.ItemId, itemTitle = i.Title, type = i.Type.ToString()
                })
                .ToListAsync());
        });

        // ----- Certificates -----
        g.MapGet("/certificates", async (ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = CurrentUser.Id(principal);
            return Results.Ok(await db.Certificates
                .Where(c => c.UserId == userId)
                .Join(db.Courses, c => c.CourseId, k => k.Id,
                    (c, k) => new { c.Code, c.IssuedAt, courseTitle = k.Title })
                .ToListAsync());
        });

        g.MapPost("/certificates/claim/{slug}", async (string slug, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var course = await db.Courses.FirstOrDefaultAsync(c => c.Slug == slug);
            if (course is null) return Results.NotFound();

            var userId = CurrentUser.Id(principal);
            var itemIds = await db.Items
                .Where(i => i.Section.CourseId == course.Id && i.CountsTowardCompletion)
                .Select(i => i.Id).ToListAsync();
            var done = await db.Progress.CountAsync(p =>
                p.UserId == userId && p.Completed && itemIds.Contains(p.ItemId));

            if (itemIds.Count == 0 || done < itemIds.Count)
                return Results.BadRequest(new { error = $"Course not complete: {done}/{itemIds.Count} items done." });

            var existing = await db.Certificates.FirstOrDefaultAsync(c =>
                c.UserId == userId && c.CourseId == course.Id);
            if (existing is not null)
                return Results.Ok(new { existing.Code, existing.IssuedAt, course.Title });

            var cert = new Certificate
            {
                UserId = userId, CourseId = course.Id,
                Code = $"JSA-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}"
            };
            db.Certificates.Add(cert);

            var enr = await db.Enrollments.FirstOrDefaultAsync(e => e.UserId == userId && e.CourseId == course.Id);
            if (enr is not null) enr.CompletedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            return Results.Ok(new { cert.Code, cert.IssuedAt, course.Title });
        });

        // Public verification (outside /me group)
        app.MapGet("/certificates/{code}", async (string code, AppDbContext db) =>
        {
            var result = await db.Certificates
                .Where(c => c.Code == code.ToUpperInvariant())
                .Join(db.Users, c => c.UserId, u => u.Id, (c, u) => new { c, u.DisplayName })
                .Join(db.Courses, x => x.c.CourseId, k => k.Id,
                    (x, k) => new { x.c.Code, x.c.IssuedAt, x.DisplayName, courseTitle = k.Title })
                .FirstOrDefaultAsync();
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
    }
}
