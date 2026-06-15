using Accelerator.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Accelerator.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Section> Sections => Set<Section>();
    public DbSet<LessonItem> Items => Set<LessonItem>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<ItemProgress> Progress => Set<ItemProgress>();
    public DbSet<QuizAttempt> QuizAttempts => Set<QuizAttempt>();
    public DbSet<AssignmentSubmission> Submissions => Set<AssignmentSubmission>();
    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<DiscussionThread> Threads => Set<DiscussionThread>();
    public DbSet<DiscussionReply> Replies => Set<DiscussionReply>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Bookmark> Bookmarks => Set<Bookmark>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(x => x.Email).IsUnique();
        b.Entity<RefreshToken>().HasIndex(x => x.Token).IsUnique();
        b.Entity<RefreshToken>().HasIndex(x => x.UserId);

        b.Entity<Course>().HasIndex(x => x.Slug).IsUnique();
        b.Entity<Course>().HasIndex(x => new { x.Status, x.Category });
        b.Entity<Course>().Property(x => x.OutcomesJson).HasColumnType("jsonb");
        b.Entity<Course>().Property(x => x.PrerequisitesJson).HasColumnType("jsonb");
        b.Entity<Course>().Property(x => x.TargetAudienceJson).HasColumnType("jsonb");
        b.Entity<Course>()
            .HasOne(c => c.Instructor).WithMany()
            .HasForeignKey(c => c.InstructorId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<Section>()
            .HasOne(s => s.Course).WithMany(c => c.Sections)
            .HasForeignKey(s => s.CourseId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Section>().HasIndex(x => new { x.CourseId, x.Order });

        b.Entity<LessonItem>()
            .HasOne(i => i.Section).WithMany(s => s.Items)
            .HasForeignKey(i => i.SectionId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LessonItem>().HasIndex(x => new { x.SectionId, x.Order });
        b.Entity<LessonItem>().Property(x => x.PayloadJson).HasColumnType("jsonb");

        b.Entity<Enrollment>().HasIndex(x => new { x.UserId, x.CourseId }).IsUnique();
        b.Entity<ItemProgress>().HasIndex(x => new { x.UserId, x.ItemId }).IsUnique();
        b.Entity<QuizAttempt>().HasIndex(x => new { x.UserId, x.ItemId });
        b.Entity<AssignmentSubmission>().HasIndex(x => new { x.UserId, x.ItemId });
        b.Entity<Certificate>().HasIndex(x => x.Code).IsUnique();
        b.Entity<Certificate>().HasIndex(x => new { x.UserId, x.CourseId }).IsUnique();

        b.Entity<Review>()
            .HasOne(r => r.Course).WithMany(c => c.Reviews)
            .HasForeignKey(r => r.CourseId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Review>().HasIndex(x => new { x.CourseId, x.UserId }).IsUnique();

        b.Entity<DiscussionReply>()
            .HasOne(r => r.Thread).WithMany(t => t.Replies)
            .HasForeignKey(r => r.ThreadId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DiscussionThread>().HasIndex(x => x.ItemId);

        b.Entity<Note>().HasIndex(x => new { x.UserId, x.ItemId });
        b.Entity<Bookmark>().HasIndex(x => new { x.UserId, x.ItemId }).IsUnique();
    }
}
