using InvoiceManager.Models.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace InvoiceManager.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<TaxAccount> TaxAccounts => Set<TaxAccount>();
        public DbSet<UserTaxAccount> UserTaxAccounts => Set<UserTaxAccount>();
        public DbSet<Invoice> Invoices => Set<Invoice>();
        public DbSet<InvoiceDetail> InvoiceDetails => Set<InvoiceDetail>();
        public DbSet<SyncLog> SyncLogs => Set<SyncLog>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<FeatureRequest> FeatureRequests => Set<FeatureRequest>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // Index duy nhất chống trùng lặp hóa đơn theo nguyên tắc Idempotent:
            // 1 công ty (TaxAccountId), 1 loại hóa đơn (Mua vào/Bán ra), cùng 1 người bán (SellerTaxCode), cùng Ký hiệu và Số HĐ
            builder.Entity<Invoice>()
                .HasIndex(i => new { i.TaxAccountId, i.InvoiceType, i.SellerTaxCode, i.InvoiceSymbol, i.InvoiceNumber })
                .IsUnique();

            // Index hỗ trợ tìm kiếm và lọc nhanh theo ngày lập và MST
            builder.Entity<Invoice>()
                .HasIndex(i => new { i.TaxAccountId, i.InvoiceType, i.IssueDate });

            builder.Entity<Invoice>()
                .HasIndex(i => i.SellerTaxCode);

            builder.Entity<Invoice>()
                .HasIndex(i => i.BuyerTaxCode);

            // Cấu hình quan hệ
            builder.Entity<TaxAccount>()
                .HasMany(t => t.Invoices)
                .WithOne(i => i.TaxAccount)
                .HasForeignKey(i => i.TaxAccountId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Invoice>()
                .HasMany(i => i.Details)
                .WithOne(d => d.Invoice)
                .HasForeignKey(d => d.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<UserTaxAccount>()
                .HasOne(u => u.User)
                .WithMany(u => u.UserTaxAccounts)
                .HasForeignKey(u => u.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<UserTaxAccount>()
                .HasOne(u => u.TaxAccount)
                .WithMany(t => t.UserTaxAccounts)
                .HasForeignKey(u => u.TaxAccountId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<AuditLog>()
                .HasOne(a => a.TaxAccount)
                .WithMany()
                .HasForeignKey(a => a.TaxAccountId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.Entity<SyncLog>()
                .HasOne(s => s.TaxAccount)
                .WithMany(t => t.SyncLogs)
                .HasForeignKey(s => s.TaxAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
