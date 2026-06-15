using System.Text.Json;
using Accelerator.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Accelerator.Api.Data;

public static class DbSeeder
{
    // ----- Seed file shapes (course.json) -----
    private record SeedOption(string Id, string Text, bool Correct);
    private record SeedQuestion(string Id, string Text, string Kind, List<SeedOption> Options, string? Explanation);
    private record SeedQuiz(int PassPercent, List<SeedQuestion> Questions);
    private record SeedRubric(string Criterion, int Points);
    private record SeedAssignment(string InstructionsFile, int TotalPoints, List<SeedRubric> Rubric, string SubmissionHint);
    private record SeedVideo(string Url, int DurationSec, string? NotesFile);
    private record SeedTest(string Name, string? Stdin, string ExpectedStdout);
    private record SeedCode(string Language, string PromptFile, string StarterCode, List<SeedTest> Tests, string? SolutionCode, string? HintMarkdown);
    private record SeedResourceLink(string Title, string Url);
    private record SeedResource(string Description, List<SeedResourceLink> Links);
    private record SeedItem(string Type, string Title, int EstMinutes, bool IsFreePreview, bool? CountsTowardCompletion,
        string? ContentFile, SeedVideo? Video, SeedQuiz? Quiz, SeedAssignment? Assignment, SeedCode? Code, SeedResource? Resource);
    private record SeedSection(string Code, string Title, string Track, string WeekRange, string Summary, List<SeedItem> Items);
    private record SeedCourse(string Slug, string Title, string Subtitle, string DescriptionFile, string Category,
        string Level, string Language, int WeeksEstimate, List<string> Outcomes, List<string> Prerequisites,
        List<string> TargetAudience, List<SeedSection> Sections);

    public static async Task SeedAsync(AppDbContext db, IConfiguration config, ILogger logger)
    {
        await db.Database.EnsureCreatedAsync();

        var adminEmail = config["Seed:AdminEmail"] ?? "admin@accelerator.local";
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Email == adminEmail);
        if (admin is null)
        {
            admin = new User
            {
                Email = adminEmail,
                DisplayName = config["Seed:AdminName"] ?? "Admin",
                Headline = config["Seed:AdminHeadline"],
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(config["Seed:AdminPassword"] ?? "ChangeMe!123"),
                Role = "Admin"
            };
            db.Users.Add(admin);
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded admin {Email}", adminEmail);
        }

        if (await db.Courses.AnyAsync()) return;

        var seedDir = Path.Combine(AppContext.BaseDirectory, "seed-data");
        var jsonPath = Path.Combine(seedDir, "course.json");
        if (!File.Exists(jsonPath))
        {
            logger.LogWarning("No seed course found at {Path}", jsonPath);
            return;
        }

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var seed = JsonSerializer.Deserialize<SeedCourse>(await File.ReadAllTextAsync(jsonPath), opts)
                   ?? throw new InvalidOperationException("course.json parse failure");

        string ReadContent(string? file, string fallback = "_Content coming soon._")
        {
            if (string.IsNullOrWhiteSpace(file)) return fallback;
            var path = Path.Combine(seedDir, "content", file);
            return File.Exists(path) ? File.ReadAllText(path) : fallback;
        }

        var course = new Course
        {
            Slug = seed.Slug,
            Title = seed.Title,
            Subtitle = seed.Subtitle,
            DescriptionMarkdown = ReadContent(seed.DescriptionFile),
            Category = seed.Category,
            Level = Enum.TryParse<CourseLevel>(seed.Level, true, out var lv) ? lv : CourseLevel.Advanced,
            Language = seed.Language,
            WeeksEstimate = seed.WeeksEstimate,
            Status = CourseStatus.Published,
            PublishedAt = DateTime.UtcNow,
            InstructorId = admin.Id,
            OutcomesJson = Payloads.Write(seed.Outcomes),
            PrerequisitesJson = Payloads.Write(seed.Prerequisites),
            TargetAudienceJson = Payloads.Write(seed.TargetAudience)
        };

        var sOrder = 0;
        foreach (var ss in seed.Sections)
        {
            var section = new Section
            {
                Order = ++sOrder, Code = ss.Code, Title = ss.Title,
                Track = ss.Track, WeekRange = ss.WeekRange, Summary = ss.Summary
            };

            var iOrder = 0;
            foreach (var si in ss.Items)
            {
                if (!Enum.TryParse<ItemType>(si.Type, true, out var type))
                    throw new InvalidOperationException($"Unknown item type '{si.Type}' in seed.");

                string payload = type switch
                {
                    ItemType.Article => Payloads.Write(new ArticlePayload(ReadContent(si.ContentFile))),
                    ItemType.Video => Payloads.Write(new VideoPayload(
                        si.Video!.Url, si.Video.DurationSec,
                        si.Video.NotesFile is null ? null : ReadContent(si.Video.NotesFile))),
                    ItemType.Quiz => Payloads.Write(new QuizPayload(
                        si.Quiz!.PassPercent,
                        si.Quiz.Questions.Select(q => new QuizQuestionP(
                            q.Id, q.Text, q.Kind,
                            q.Options.Select(o => new QuizOptionP(o.Id, o.Text, o.Correct)).ToList(),
                            q.Explanation)).ToList())),
                    ItemType.Assignment => Payloads.Write(new AssignmentPayload(
                        ReadContent(si.Assignment!.InstructionsFile),
                        si.Assignment.TotalPoints,
                        si.Assignment.Rubric.Select(r => new RubricItemP(r.Criterion, r.Points)).ToList(),
                        si.Assignment.SubmissionHint)),
                    ItemType.CodeExercise => Payloads.Write(new CodeExercisePayload(
                        si.Code!.Language,
                        ReadContent(si.Code.PromptFile),
                        si.Code.StarterCode,
                        si.Code.Tests.Select(t => new CodeTestP(t.Name, t.Stdin, t.ExpectedStdout)).ToList(),
                        si.Code.SolutionCode,
                        si.Code.HintMarkdown)),
                    ItemType.Resource => Payloads.Write(new ResourcePayload(
                        si.Resource!.Description,
                        si.Resource.Links.Select(l => new ResourceLinkP(l.Title, l.Url)).ToList())),
                    _ => "{}"
                };

                section.Items.Add(new LessonItem
                {
                    Order = ++iOrder, Type = type, Title = si.Title,
                    EstMinutes = si.EstMinutes, IsFreePreview = si.IsFreePreview,
                    CountsTowardCompletion = si.CountsTowardCompletion ?? true,
                    PayloadJson = payload
                });
            }

            course.Sections.Add(section);
        }

        db.Courses.Add(course);
        await db.SaveChangesAsync();
        logger.LogInformation("Seeded course '{Title}': {Sections} sections, {Items} items",
            course.Title, course.Sections.Count, course.Sections.Sum(s => s.Items.Count));
    }
}
