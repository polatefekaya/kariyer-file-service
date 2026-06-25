using Microsoft.EntityFrameworkCore;
using Kariyer.FileService.Domain;

namespace Kariyer.FileService.Infrastructure.Persistence;

public class FileDbContext : DbContext
{
    public FileDbContext(DbContextOptions<FileDbContext> options) : base(options)
    {
    }

    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Map to a dedicated schema named "storage"
        modelBuilder.HasDefaultSchema("storage");

        modelBuilder.Entity<StoredFile>(entity =>
        {
            entity.ToTable("StoredFiles");
            entity.HasKey(e => e.Id);
            
            entity.Property(e => e.Id)
                .HasMaxLength(26); // ULID standard length

            entity.Property(e => e.StorageKey)
                .HasMaxLength(500)
                .IsRequired();

            entity.Property(e => e.OriginalFileName)
                .HasMaxLength(255)
                .IsRequired();

            entity.Property(e => e.ContentType)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(e => e.UserId)
                .HasMaxLength(36); // UUID standard length

            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValue("Pending")
                .IsRequired();

            entity.Property(e => e.UploadId)
                .HasMaxLength(100)
                .IsRequired(false);

            entity.Property(e => e.CreatedAt)
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // Indexes for lookup optimization
            entity.HasIndex(e => e.StorageKey).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.UserId, e.CreatedAt });
        });
    }
}
