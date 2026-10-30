using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class EnrollmentConfiguration : IEntityTypeConfiguration<Enrollment>
{
    public void Configure(EntityTypeBuilder<Enrollment> builder)
    {
        builder.ToTable("enrollments");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.ClassId).HasColumnName("class_id");
        builder.Property(x => x.Seats).HasColumnName("seats");
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.CancelledAt).HasColumnName("cancelled_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.RefundedVisits).HasColumnName("refunded_visits");
        builder.Property(x => x.IdempotencyKeyHash).HasColumnName("idempotency_key_hash");
        builder.Property(x => x.IdempotencyBodyHash).HasColumnName("idempotency_body_hash");
        builder.Property(x => x.IdempotencyResponseJson).HasColumnName("idempotency_response_json");
        builder.Property(x => x.IsWaitlist).HasColumnName("is_waitlist");
        builder.Property(x => x.WaitlistLeft).HasColumnName("waitlist_left");

        builder
            .HasIndex(x => new { x.UserId, x.IdempotencyKeyHash })
            .IsUnique()
            .HasFilter("idempotency_key_hash IS NOT NULL");

        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder
            .HasOne(x => x.Class)
            .WithMany(x => x.Enrollments)
            .HasForeignKey(x => x.ClassId);
    }
}
