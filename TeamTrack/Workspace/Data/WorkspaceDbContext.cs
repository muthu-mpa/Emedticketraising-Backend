using System.Text;
using Microsoft.EntityFrameworkCore;
using TeamTrack.Workspace.Models;

namespace TeamTrack.Workspace.Data
{
    /// <summary>
    /// Separate context for the ws_ tables so AppDbContext and its model snapshot stay untouched.
    /// References to users/projects are plain ids (the FKs are created by WorkspaceSchemaInitializer).
    /// </summary>
    public class WorkspaceDbContext : DbContext
    {
        public WorkspaceDbContext(DbContextOptions<WorkspaceDbContext> options) : base(options) { }

        public DbSet<WsCampaign> Campaigns => Set<WsCampaign>();
        public DbSet<WsCampaignHistory> CampaignHistory => Set<WsCampaignHistory>();
        public DbSet<WsTimeOffRequest> TimeOffRequests => Set<WsTimeOffRequest>();
        public DbSet<WsProjectDocument> ProjectDocuments => Set<WsProjectDocument>();
        public DbSet<WsNotification> Notifications => Set<WsNotification>();
        public DbSet<WsCampaignImport> CampaignImports => Set<WsCampaignImport>();
        public DbSet<WsSharedSheet> SharedSheets => Set<WsSharedSheet>();
        public DbSet<WsSharedSheetAccess> SharedSheetAccess => Set<WsSharedSheetAccess>();
        public DbSet<WsSharedSheetChange> SharedSheetChanges => Set<WsSharedSheetChange>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<WsCampaign>(e =>
            {
                e.ToTable("ws_campaigns");
                e.HasOne(c => c.Import).WithMany()
                    .HasForeignKey(c => c.ImportId).OnDelete(DeleteBehavior.SetNull);
            });
            modelBuilder.Entity<WsCampaignImport>().ToTable("ws_campaign_imports");
            modelBuilder.Entity<WsCampaignHistory>(e =>
            {
                e.ToTable("ws_campaign_history");
                e.HasOne(h => h.Campaign).WithMany(c => c.History)
                    .HasForeignKey(h => h.CampaignId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<WsTimeOffRequest>().ToTable("ws_time_off_requests");
            modelBuilder.Entity<WsProjectDocument>().ToTable("ws_project_documents");
            modelBuilder.Entity<WsNotification>().ToTable("ws_notifications");

            var jsonb = Database.IsNpgsql(); // the SQLite used by the tests has no jsonb type
            modelBuilder.Entity<WsSharedSheet>(e =>
            {
                e.ToTable("ws_shared_sheets");
                // two saves at the same version: the second UPDATE matches no row and EF throws DbUpdateConcurrencyException
                e.Property(x => x.Version).IsConcurrencyToken();
                if (jsonb) e.Property(x => x.Data).HasColumnType("jsonb");
            });
            modelBuilder.Entity<WsSharedSheetAccess>(e =>
            {
                e.ToTable("ws_shared_sheet_access");
                e.HasKey(x => new { x.SheetId, x.UserId });
            });
            modelBuilder.Entity<WsSharedSheetChange>(e =>
            {
                e.ToTable("ws_shared_sheet_changes");
                if (jsonb) e.Property(x => x.ChangedCells).HasColumnType("jsonb");
            });

            // snake_case columns, matching the rest of the schema
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entity.GetProperties())
                {
                    property.SetColumnName(ToSnakeCase(property.Name));
                }
            }
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
