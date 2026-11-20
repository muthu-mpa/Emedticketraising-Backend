using System.Text;
using Microsoft.EntityFrameworkCore;
using TeamTrack.Marketing.Models;

namespace TeamTrack.Marketing.Data
{
    /// <summary>
    /// Separate context for the mk_ tables so AppDbContext and its model snapshot stay untouched.
    /// User references are plain ids (the FKs are created by MarketingSchemaInitializer).
    /// Keep the model free of Postgres-only features: the tests run it on SQLite.
    /// </summary>
    public class MarketingDbContext : DbContext
    {
        public MarketingDbContext(DbContextOptions<MarketingDbContext> options) : base(options) { }

        public DbSet<MkLead> Leads => Set<MkLead>();
        public DbSet<MkLeadHistoryEntry> History => Set<MkLeadHistoryEntry>();
        public DbSet<MkLeadAttachment> Attachments => Set<MkLeadAttachment>();
        public DbSet<MkActivity> Activity => Set<MkActivity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MkLead>().ToTable("mk_leads");
            modelBuilder.Entity<MkLeadHistoryEntry>(e =>
            {
                e.ToTable("mk_lead_history");
                e.HasOne(h => h.Lead).WithMany(l => l.History).HasForeignKey(h => h.LeadId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(h => new { h.LeadId, h.OccurredAt });
            });
            modelBuilder.Entity<MkLeadAttachment>(e =>
            {
                e.ToTable("mk_lead_attachments");
                e.HasOne(a => a.HistoryEntry).WithMany(h => h.Attachments).HasForeignKey(a => a.HistoryEntryId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<MkActivity>().ToTable("mk_activity");

            // snake_case columns, matching the rest of the schema
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
                foreach (var property in entity.GetProperties())
                    property.SetColumnName(ToSnakeCase(property.Name));
        }

        private static string ToSnakeCase(string name)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0) sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
