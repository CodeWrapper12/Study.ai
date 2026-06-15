namespace Accelerator.Api.Domain;

// ---------- Identity ----------
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = default!;
    public string PasswordHash { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string Role { get; set; } = "Student"; // Student | Instructor | Admin
    public string? Headline { get; set; }          // shown on reviews/discussions
    public int Xp { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Token { get; set; } = default!;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByToken { get; set; }
    public bool IsActive => RevokedAt is null && DateTime.UtcNow < ExpiresAt;
}

// ---------- Catalog ----------
public enum CourseStatus { Draft = 0, Published = 1, Archived = 2 }
public enum CourseLevel { Beginner = 0, Intermediate = 1, Advanced = 2, Expert = 3 }

public class Course
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string Subtitle { get; set; } = "";
    public string DescriptionMarkdown { get; set; } = "";
    public string Category { get; set; } = "Engineering";
    public CourseLevel Level { get; set; } = CourseLevel.Advanced;
    public string Language { get; set; } = "English";
    public string? ThumbnailUrl { get; set; }
    public string? PromoVideoUrl { get; set; }
    public CourseStatus Status { get; set; } = CourseStatus.Draft;
    public int WeeksEstimate { get; set; }

    // Stored as JSON arrays (jsonb)
    public string OutcomesJson { get; set; } = "[]";
    public string PrerequisitesJson { get; set; } = "[]";
    public string TargetAudienceJson { get; set; } = "[]";

    public Guid InstructorId { get; set; }
    public User Instructor { get; set; } = default!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }

    public List<Section> Sections { get; set; } = new();
    public List<Review> Reviews { get; set; } = new();
}

public class Section
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public Course Course { get; set; } = default!;
    public int Order { get; set; }
    public string Code { get; set; } = "";       // e.g. "M1"
    public string Title { get; set; } = default!;
    public string Summary { get; set; } = "";
    public string Track { get; set; } = "";      // optional label, e.g. Interview/Skills
    public string WeekRange { get; set; } = "";
    public List<LessonItem> Items { get; set; } = new();
}

public enum ItemType { Article = 0, Video = 1, Quiz = 2, Assignment = 3, CodeExercise = 4, Resource = 5 }

public class LessonItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SectionId { get; set; }
    public Section Section { get; set; } = default!;
    public int Order { get; set; }
    public ItemType Type { get; set; }
    public string Title { get; set; } = default!;
    public int EstMinutes { get; set; } = 15;
    public bool IsFreePreview { get; set; }
    public bool CountsTowardCompletion { get; set; } = true;

    /// <summary>Typed payload serialized as JSON (jsonb). Shape depends on Type — see Payloads.cs.</summary>
    public string PayloadJson { get; set; } = "{}";
}

// ---------- Learning ----------
public class Enrollment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid CourseId { get; set; }
    public DateTime EnrolledAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public Guid? LastItemId { get; set; }       // resume position
}

public class ItemProgress
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid ItemId { get; set; }
    public bool Completed { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? BestScorePercent { get; set; }  // quizzes
    public int VideoPositionSec { get; set; }   // videos
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class QuizAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid ItemId { get; set; }
    public int ScorePercent { get; set; }
    public bool Passed { get; set; }
    public DateTime AttemptedAt { get; set; } = DateTime.UtcNow;
}

public enum SubmissionStatus { Submitted = 0, Graded = 1, ChangesRequested = 2 }

public class AssignmentSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid ItemId { get; set; }
    public string ContentText { get; set; } = "";   // write-up / repo URL / notes
    public string? ArtifactUrl { get; set; }
    public SubmissionStatus Status { get; set; } = SubmissionStatus.Submitted;
    public int? GradePercent { get; set; }
    public string? Feedback { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime? GradedAt { get; set; }
}

public class Certificate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid CourseId { get; set; }
    public string Code { get; set; } = default!;
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
}

// ---------- Community ----------
public class Review
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public Course Course { get; set; } = default!;
    public Guid UserId { get; set; }
    public int Rating { get; set; }              // 1..5
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class DiscussionThread
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ItemId { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = default!;
    public string Body { get; set; } = "";
    public bool IsResolved { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<DiscussionReply> Replies { get; set; } = new();
}

public class DiscussionReply
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ThreadId { get; set; }
    public DiscussionThread Thread { get; set; } = default!;
    public Guid UserId { get; set; }
    public string Body { get; set; } = default!;
    public bool IsInstructorReply { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid ItemId { get; set; }
    public string Body { get; set; } = default!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Bookmark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid ItemId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
