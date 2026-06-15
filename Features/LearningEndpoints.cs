using System.Security.Claims;
using Accelerator.Api.Common;
using Accelerator.Api.Data;
using Accelerator.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Accelerator.Api.Features;

public static class LearningEndpoints
{
    private const int XpItem = 50;
    private const int XpQuizPass = 25;
    public const int XpPerLevel = 500;

    public static void MapLearningEndpoints(this WebApplication app)
    {
        app.MapPost("/learn/{slug}/enroll", async (string slug, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var course = await db.Courses.FirstOrDefaultAsync(c => c.Slug == slug && c.Status == CourseStatus.Published);
            if (course is null) return Results.NotFound();

            var userId = CurrentUser.Id(principal);
            if (!await db.Enrollments.AnyAsync(e => e.UserId == userId && e.CourseId == course.Id))
            {
                db.Enrollments.Add(new Enrollment { UserId = userId, CourseId = course.Id });
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { enrolled = true });
        }).RequireAuthorization();

        // Player shell: full outline + per-item progress + resume pointer
        app.MapGet("/learn/{slug}", async (string slug, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var course = await db.Courses
                .Include(c => c.Sections.OrderBy(s => s.Order))
                .ThenInclude(s => s.Items.OrderBy(i => i.Order))
                .FirstOrDefaultAsync(c => c.Slug == slug && c.Status == CourseStatus.Published);
            if (course is null) return Results.NotFound();

            var userId = CurrentUser.Id(principal);
            var enrollment = await db.Enrollments.FirstOrDefaultAsync(e => e.UserId == userId && e.CourseId == course.Id);
            if (enrollment is null) return Results.Json(new { error = "Enroll first." }, statusCode: 403);

            var itemIds = course.Sections.SelectMany(s => s.Items).Select(i => i.Id).ToList();
            var progress = await db.Progress
                .Where(p => p.UserId == userId && itemIds.Contains(p.ItemId))
                .ToDictionaryAsync(p => p.ItemId);
            var bookmarks = (await db.Bookmarks
                .Where(b => b.UserId == userId && itemIds.Contains(b.ItemId))
                .Select(b => b.ItemId).ToListAsync()).ToHashSet();

            var countable = course.Sections.SelectMany(s => s.Items).Where(i => i.CountsTowardCompletion).ToList();
            var done = countable.Count(i => progress.TryGetValue(i.Id, out var p) && p.Completed);

            return Results.Ok(new
            {
                course.Slug, course.Title,
                totalItems = countable.Count, completedItems = done,
                resumeItemId = enrollment.LastItemId,
                sections = course.Sections.Select(s => new
                {
                    s.Id, s.Code, s.Title, s.Track, s.WeekRange,
                    items = s.Items.Select(i => new
                    {
                        i.Id, i.Title, type = i.Type.ToString(), i.EstMinutes,
                        completed = progress.TryGetValue(i.Id, out var p) && p.Completed,
                        bookmarked = bookmarks.Contains(i.Id)
                    })
                })
            });
        }).RequireAuthorization();

        // Item content, typed; quiz answers stripped; free previews work unauthenticated
        app.MapGet("/items/{id:guid}", async (Guid id, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var item = await db.Items
                .Include(i => i.Section).ThenInclude(s => s.Course)
                .FirstOrDefaultAsync(i => i.Id == id);
            if (item is null) return Results.NotFound();

            var userId = CurrentUser.IdOrNull(principal);
            if (userId is null && !item.IsFreePreview)
                return Results.Json(new { error = "Sign in to access this lesson." }, statusCode: 401);

            ItemProgress? prog = null;
            if (userId is not null)
            {
                prog = await db.Progress.FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == id);
                // update resume pointer
                var enr = await db.Enrollments.FirstOrDefaultAsync(e =>
                    e.UserId == userId && e.CourseId == item.Section.CourseId);
                if (enr is not null) { enr.LastItemId = id; await db.SaveChangesAsync(); }
            }

            // ordered course items for prev/next
            var ordered = await db.Items
                .Where(i => i.Section.CourseId == item.Section.CourseId)
                .OrderBy(i => i.Section.Order).ThenBy(i => i.Order)
                .Select(i => i.Id).ToListAsync();
            var idx = ordered.IndexOf(id);

            object? payload = item.Type switch
            {
                ItemType.Article => Payloads.Read<ArticlePayload>(item.PayloadJson),
                ItemType.Video => Payloads.Read<VideoPayload>(item.PayloadJson),
                ItemType.Resource => Payloads.Read<ResourcePayload>(item.PayloadJson),
                ItemType.Assignment => Payloads.Read<AssignmentPayload>(item.PayloadJson),
                ItemType.CodeExercise => StripCode(Payloads.Read<CodeExercisePayload>(item.PayloadJson)!,
                    revealSolution: prog?.Completed == true),
                ItemType.Quiz => StripQuiz(Payloads.Read<QuizPayload>(item.PayloadJson)!),
                _ => null
            };

            AssignmentSubmission? submission = null;
            if (item.Type == ItemType.Assignment && userId is not null)
                submission = await db.Submissions
                    .Where(s => s.UserId == userId && s.ItemId == id)
                    .OrderByDescending(s => s.SubmittedAt).FirstOrDefaultAsync();

            return Results.Ok(new
            {
                item.Id, item.Title, type = item.Type.ToString(), item.EstMinutes,
                section = new { item.Section.Code, item.Section.Title, item.Section.Track },
                courseSlug = item.Section.Course.Slug,
                completed = prog?.Completed ?? false,
                bestScorePercent = prog?.BestScorePercent,
                videoPositionSec = prog?.VideoPositionSec ?? 0,
                prevItemId = idx > 0 ? ordered[idx - 1] : (Guid?)null,
                nextItemId = idx >= 0 && idx < ordered.Count - 1 ? ordered[idx + 1] : (Guid?)null,
                payload,
                submission = submission is null ? null : new
                {
                    submission.Id, submission.ContentText, submission.ArtifactUrl,
                    status = submission.Status.ToString(), submission.GradePercent,
                    submission.Feedback, submission.SubmittedAt
                }
            });
        });

        app.MapPost("/items/{id:guid}/complete", async (Guid id, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var item = await db.Items.FindAsync(id);
            if (item is null) return Results.NotFound();

            var userId = CurrentUser.Id(principal);
            var prog = await db.Progress.FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == id);
            if (prog?.Completed == true) return Results.Ok(new { completed = true, xpAwarded = 0 });

            prog ??= db.Progress.Add(new ItemProgress { UserId = userId, ItemId = id }).Entity;
            prog.Completed = true;
            prog.CompletedAt = DateTime.UtcNow;
            prog.UpdatedAt = DateTime.UtcNow;

            var user = await db.Users.FindAsync(userId);
            user!.Xp += XpItem;
            await db.SaveChangesAsync();

            return Results.Ok(new { completed = true, xpAwarded = XpItem, totalXp = user.Xp });
        }).RequireAuthorization();

        app.MapPost("/items/{id:guid}/video-position",
            async (Guid id, VideoPositionRequest req, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var userId = CurrentUser.Id(principal);
            var prog = await db.Progress.FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == id)
                ?? db.Progress.Add(new ItemProgress { UserId = userId, ItemId = id }).Entity;
            prog.VideoPositionSec = Math.Max(0, req.PositionSec);
            prog.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization();

        app.MapPost("/items/{id:guid}/quiz-attempt",
            async (Guid id, QuizSubmission submission, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id && i.Type == ItemType.Quiz);
            if (item is null) return Results.NotFound();

            var quiz = Payloads.Read<QuizPayload>(item.PayloadJson)!;
            if (quiz.Questions.Count == 0) return Results.BadRequest(new { error = "Quiz has no questions." });

            var answers = submission.Answers.ToDictionary(a => a.QuestionId, a => a.OptionIds.ToHashSet());
            var correct = 0;
            var perQuestion = new List<object>();
            foreach (var q in quiz.Questions)
            {
                var correctIds = q.Options.Where(o => o.Correct).Select(o => o.Id).ToHashSet();
                var given = answers.GetValueOrDefault(q.Id, new HashSet<string>());
                var isCorrect = correctIds.SetEquals(given);
                if (isCorrect) correct++;
                perQuestion.Add(new { questionId = q.Id, correct = isCorrect, explanation = q.Explanation });
            }

            var score = (int)Math.Round(100.0 * correct / quiz.Questions.Count);
            var passed = score >= quiz.PassPercent;
            var userId = CurrentUser.Id(principal);

            db.QuizAttempts.Add(new QuizAttempt { UserId = userId, ItemId = id, ScorePercent = score, Passed = passed });

            var prog = await db.Progress.FirstOrDefaultAsync(p => p.UserId == userId && p.ItemId == id)
                ?? db.Progress.Add(new ItemProgress { UserId = userId, ItemId = id }).Entity;
            var firstPass = passed && prog.Completed == false;
            prog.BestScorePercent = Math.Max(prog.BestScorePercent ?? 0, score);
            prog.UpdatedAt = DateTime.UtcNow;

            var xp = 0;
            if (firstPass)
            {
                prog.Completed = true;
                prog.CompletedAt = DateTime.UtcNow;
                var user = await db.Users.FindAsync(userId);
                user!.Xp += XpQuizPass + XpItem;
                xp = XpQuizPass + XpItem;
            }
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                score, passed, quiz.PassPercent, correct,
                total = quiz.Questions.Count, xpAwarded = xp, perQuestion
            });
        }).RequireAuthorization();

        app.MapPost("/items/{id:guid}/submit-assignment",
            async (Guid id, AssignmentRequest req, ClaimsPrincipal principal, AppDbContext db) =>
        {
            var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id && i.Type == ItemType.Assignment);
            if (item is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.ContentText))
                return Results.BadRequest(new { error = "Write a short submission note (what you built, decisions, links)." });

            var userId = CurrentUser.Id(principal);
            db.Submissions.Add(new AssignmentSubmission
            {
                UserId = userId, ItemId = id,
                ContentText = req.ContentText.Trim(),
                ArtifactUrl = string.IsNullOrWhiteSpace(req.ArtifactUrl) ? null : req.ArtifactUrl.Trim()
            });
            await db.SaveChangesAsync();
            return Results.Ok(new { submitted = true });
        }).RequireAuthorization();
    }

    private static QuizPayload StripQuiz(QuizPayload quiz) => quiz with
    {
        Questions = quiz.Questions
            .Select(q => q with
            {
                Explanation = null,
                Options = q.Options.Select(o => o with { Correct = false }).ToList()
            }).ToList()
    };

    private static CodeExercisePayload StripCode(CodeExercisePayload code, bool revealSolution) =>
        revealSolution ? code : code with { SolutionCode = null };

    public record QuizAnswer(string QuestionId, List<string> OptionIds);
    public record QuizSubmission(List<QuizAnswer> Answers);
    public record VideoPositionRequest(int PositionSec);
    public record AssignmentRequest(string ContentText, string? ArtifactUrl);
}
