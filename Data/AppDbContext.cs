using Microsoft.EntityFrameworkCore;
using live_poll_backend.Models.Entities;
using live_poll_backend.Models.Enums;

namespace live_poll_backend.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Poll> Polls => Set<Poll>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Option> Options => Set<Option>();
    public DbSet<VoteCount> VoteCounts => Set<VoteCount>();
    public DbSet<Vote> Votes => Set<Vote>();
    public DbSet<User> Users => Set<User>();
    public DbSet<WordCloudCount> WordCloudCounts => Set<WordCloudCount>();
    public DbSet<BiddingQuestion> BiddingQuestions => Set<BiddingQuestion>();
    public DbSet<BiddingSkill> BiddingSkills => Set<BiddingSkill>();
    public DbSet<SkillBid> SkillBids => Set<SkillBid>();
    public DbSet<BiddingPoll> BiddingPolls => Set<BiddingPoll>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Poll ──
        modelBuilder.Entity<Poll>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(10);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(500);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(200);
            entity.Property(e => e.CreatedByEmail).HasMaxLength(300);
            entity.Property(e => e.CreatedByName).HasMaxLength(200);
            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(20)
                .HasDefaultValue(PollStatus.Draft);
            entity.Property(e => e.ActiveQuestionIndex).HasDefaultValue(-1);
            entity.Property(e => e.CurrentQuestionActive).HasDefaultValue(false);
            entity.Property(e => e.Theme).HasMaxLength(50).HasDefaultValue("default");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("NOW()");
        });

        // ── Question ──
        modelBuilder.Entity<Question>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Text).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.Index).IsRequired();
            entity.Property(e => e.Type)
                .HasConversion<string>()
                .HasMaxLength(20)
                .HasDefaultValue(QuestionType.MultipleChoice);

            entity.HasOne(e => e.Poll)
                .WithMany(p => p.Questions)
                .HasForeignKey(e => e.PollId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Option ──
        modelBuilder.Entity<Option>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Text).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Index).IsRequired();

            entity.HasOne(e => e.Question)
                .WithMany(q => q.Options)
                .HasForeignKey(e => e.QuestionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── VoteCount ──
        modelBuilder.Entity<VoteCount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Count).HasDefaultValue(0);

            entity.HasIndex(e => new { e.PollId, e.QuestionIndex, e.OptionIndex }).IsUnique();

            entity.HasOne(e => e.Poll)
                .WithMany(p => p.VoteCounts)
                .HasForeignKey(e => e.PollId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Vote ──
        modelBuilder.Entity<Vote>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SessionId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.VotedAt).HasDefaultValueSql("NOW()");
            entity.Property(e => e.OptionIndex).IsRequired(false);

            // One vote per session per question per poll
            entity.HasIndex(e => new { e.PollId, e.QuestionIndex, e.SessionId }).IsUnique();

            entity.HasOne(e => e.Poll)
                .WithMany(p => p.Votes)
                .HasForeignKey(e => e.PollId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── WordCloudCount ──
        modelBuilder.Entity<WordCloudCount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Word).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Count).HasDefaultValue(0);

            entity.HasIndex(e => new { e.PollId, e.QuestionIndex, e.Word }).IsUnique();

            entity.HasOne(e => e.Poll)
                .WithMany(p => p.WordCloudCounts)
                .HasForeignKey(e => e.PollId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── User ──
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(300);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.PasswordHash).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
        });

        // ── BiddingQuestion ──
        modelBuilder.Entity<BiddingQuestion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Text).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.Index).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");

            entity.HasOne(e => e.BiddingPoll)
                .WithMany(p => p.Questions)
                .HasForeignKey(e => e.BiddingPollId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── BiddingSkill ──
        modelBuilder.Entity<BiddingSkill>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Category).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Index).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");

            entity.HasOne(e => e.BiddingQuestion)
                .WithMany(q => q.Skills)
                .HasForeignKey(e => e.BiddingQuestionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── SkillBid ──
        modelBuilder.Entity<SkillBid>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SessionId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Cohort).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CoinsSpent).IsRequired();
            entity.Property(e => e.IsCommitted).HasDefaultValue(false);
            entity.Property(e => e.QuestionIndex).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");

            entity.HasIndex(e => new { e.BiddingPollId, e.BiddingSkillId, e.SessionId, e.Cohort }).IsUnique();
            entity.HasIndex(e => new { e.BiddingPollId, e.SessionId, e.Cohort });

            entity.HasOne(e => e.BiddingPoll)
                .WithMany()
                .HasForeignKey(e => e.BiddingPollId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.BiddingSkill)
                .WithMany()
                .HasForeignKey(e => e.BiddingSkillId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── BiddingPoll ──
        modelBuilder.Entity<BiddingPoll>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(10);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(500);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(200);
            entity.Property(e => e.CreatedByEmail).HasMaxLength(300);
            entity.Property(e => e.CreatedByName).HasMaxLength(200);
            entity.Property(e => e.IsBiddingActive).HasDefaultValue(false);
            entity.Property(e => e.BiddingClosed).HasDefaultValue(false);
            entity.Property(e => e.Theme).HasMaxLength(50).HasDefaultValue("synergy_sphere");
            entity.Property(e => e.ActiveQuestionIndex).HasDefaultValue(-1);
            entity.Property(e => e.CurrentCohort).HasMaxLength(50).HasDefaultValue("");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("NOW()");
        });
    }
}
