using System;
using System.Collections.Generic;
using Berichtsportal.Models;
using Microsoft.EntityFrameworkCore;

namespace Berichtsportal.Data;

public partial class BerichtsportalDbContext : DbContext
{
    public BerichtsportalDbContext()
    {
    }

    public BerichtsportalDbContext(DbContextOptions<BerichtsportalDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Comment> Comments { get; set; }

    public virtual DbSet<Report> Reports { get; set; }

    public virtual DbSet<Role> Roles { get; set; }

    public virtual DbSet<User> Users { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
#warning To protect potentially sensitive information in your connection string, you should move it out of source code. You can avoid scaffolding the connection string by using the Name= syntax to read it from configuration - see https://go.microsoft.com/fwlink/?linkid=2131148. For more guidance on storing connection strings, see https://go.microsoft.com/fwlink/?LinkId=723263.
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlServer(
                "Server=(localdb)\\mssqllocaldb;Database=BerichtsportalDB;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True",
                sqlOptions => sqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null));
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Comment>(entity =>
        {
            entity.HasKey(e => e.CommentId).HasName("PK__Comments__C3B4DFAA4CB41F8E");

            entity.Property(e => e.CommentId).HasColumnName("CommentID");
            entity.Property(e => e.CommentorId).HasColumnName("CommentorID");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("(getdate())")
                .HasColumnType("datetime");
            entity.Property(e => e.ReportId).HasColumnName("ReportID");

            entity.HasOne(d => d.Commentor).WithMany(p => p.Comments)
                .HasForeignKey(d => d.CommentorId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Comment_User");

            entity.HasOne(d => d.Report).WithMany(p => p.Comments)
                .HasForeignKey(d => d.ReportId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Comment_Report");
        });

        modelBuilder.Entity<Report>(entity =>
        {
            entity.HasKey(e => e.ReportId).HasName("PK__Reports__D5BD48E5D047AE7D");

            entity.Property(e => e.ReportId).HasColumnName("ReportID");
            entity.Property(e => e.AzubiId).HasColumnName("AzubiID");
            entity.Property(e => e.LastModified)
                .HasDefaultValueSql("(getdate())")
                .HasColumnType("datetime");

            entity.HasOne(d => d.Azubi).WithMany(p => p.Reports)
                .HasForeignKey(d => d.AzubiId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Report_Azubi");
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(e => e.RoleId).HasName("PK__Roles__8AFACE3AC8322077");

            entity.Property(e => e.RoleId)
                .ValueGeneratedNever()
                .HasColumnName("RoleID");
            entity.Property(e => e.RoleName).HasMaxLength(50);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.UserId).HasName("PK__Users__1788CCAC53B09DF9");

            entity.HasIndex(e => e.Email, "UQ__Users__A9D10534B9800BD9").IsUnique();

            entity.Property(e => e.UserId)
                .ValueGeneratedNever()
                .HasColumnName("UserID");
            entity.Property(e => e.Email).HasMaxLength(255);
            entity.Property(e => e.FirstName).HasMaxLength(50);
            entity.Property(e => e.LastName).HasMaxLength(50);
            entity.Property(e => e.MentorId).HasColumnName("MentorID");
            entity.Property(e => e.RoleId).HasColumnName("RoleID");

            entity.HasOne(d => d.Mentor).WithMany(p => p.InverseMentor)
                .HasForeignKey(d => d.MentorId)
                .HasConstraintName("FK_User_Mentor");

            entity.HasOne(d => d.Role).WithMany(p => p.Users)
                .HasForeignKey(d => d.RoleId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_User_Role");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}

