using Microsoft.EntityFrameworkCore;

namespace TeamTrack.Marketing.Data
{
    /// <summary>Tracks whether the mk_ schema is usable. Registered only when Features:Marketing is on.</summary>
    public class MarketingState
    {
        public bool SchemaReady { get; set; }
        public string? SchemaError { get; set; }
    }

    public static class MarketingSchemaInitializer
    {
        // Run through ExecuteSqlRawAsync, which treats { and } as format placeholders: never put braces in this script.
        public const string Sql = @"
            CREATE TABLE IF NOT EXISTS mk_leads (
                id uuid NOT NULL,
                name text NOT NULL,
                title text NOT NULL,
                company text NOT NULL,
                email text NOT NULL,
                phone text,
                channel text NOT NULL,
                campaign text NOT NULL,
                interest text NOT NULL,
                score integer NOT NULL,
                stage text NOT NULL,
                close_reason text,
                consent boolean NOT NULL DEFAULT false,
                owner_user_id integer,
                created_at timestamp with time zone NOT NULL,
                picked_up_at timestamp with time zone,
                updated_at timestamp with time zone NOT NULL,
                CONSTRAINT ""PK_mk_leads"" PRIMARY KEY (id),
                CONSTRAINT ""FK_mk_leads_users_owner_user_id"" FOREIGN KEY (owner_user_id) REFERENCES users (id) ON DELETE SET NULL,
                CONSTRAINT ""CK_mk_leads_score"" CHECK (score BETWEEN 0 AND 100),
                CONSTRAINT ""CK_mk_leads_stage"" CHECK (stage IN ('New','Contacted','Replied','Interested','Closed'))
            );
            ALTER TABLE mk_leads ADD COLUMN IF NOT EXISTS org_phone text;
            ALTER TABLE mk_leads ADD COLUMN IF NOT EXISTS linked_in_url text;
            ALTER TABLE mk_leads ADD COLUMN IF NOT EXISTS possible_email text;
            ALTER TABLE mk_leads ADD COLUMN IF NOT EXISTS org_email text;
            ALTER TABLE mk_leads ADD COLUMN IF NOT EXISTS additional_data text;
            ALTER TABLE mk_leads ADD COLUMN IF NOT EXISTS import_id uuid;
            CREATE INDEX IF NOT EXISTS ""IX_mk_leads_import_id"" ON mk_leads (import_id);
            CREATE INDEX IF NOT EXISTS ""IX_mk_leads_owner_user_id"" ON mk_leads (owner_user_id);
            CREATE INDEX IF NOT EXISTS ""IX_mk_leads_stage"" ON mk_leads (stage);

            CREATE TABLE IF NOT EXISTS mk_lead_history (
                id uuid NOT NULL,
                lead_id uuid NOT NULL,
                kind text NOT NULL,
                occurred_at timestamp with time zone NOT NULL,
                subject text,
                body text,
                ""text"" text,
                created_by_user_id integer,
                created_at timestamp with time zone NOT NULL,
                CONSTRAINT ""PK_mk_lead_history"" PRIMARY KEY (id),
                CONSTRAINT ""FK_mk_lead_history_mk_leads_lead_id"" FOREIGN KEY (lead_id) REFERENCES mk_leads (id) ON DELETE CASCADE,
                CONSTRAINT ""FK_mk_lead_history_users_created_by_user_id"" FOREIGN KEY (created_by_user_id) REFERENCES users (id) ON DELETE SET NULL
            );
            CREATE INDEX IF NOT EXISTS ""IX_mk_lead_history_lead_id_occurred_at"" ON mk_lead_history (lead_id, occurred_at);

            CREATE TABLE IF NOT EXISTS mk_lead_attachments (
                id uuid NOT NULL,
                history_entry_id uuid NOT NULL,
                file_name text NOT NULL,
                content_type text NOT NULL,
                size_bytes bigint NOT NULL,
                storage_path text NOT NULL,
                signed_by text,
                signed_at timestamp with time zone,
                created_at timestamp with time zone NOT NULL,
                CONSTRAINT ""PK_mk_lead_attachments"" PRIMARY KEY (id),
                CONSTRAINT ""FK_mk_lead_attachments_mk_lead_history_history_entry_id"" FOREIGN KEY (history_entry_id) REFERENCES mk_lead_history (id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ""IX_mk_lead_attachments_history_entry_id"" ON mk_lead_attachments (history_entry_id);

            CREATE TABLE IF NOT EXISTS mk_lead_imports (
                id uuid NOT NULL,
                file_name text NOT NULL,
                size_bytes bigint NOT NULL,
                total_rows integer NOT NULL,
                imported integer NOT NULL,
                skipped integer NOT NULL,
                duplicates integer NOT NULL,
                uploaded_by_user_id integer,
                uploaded_at timestamp with time zone NOT NULL,
                undone_at timestamp with time zone,
                undone_by_user_id integer,
                CONSTRAINT ""PK_mk_lead_imports"" PRIMARY KEY (id),
                CONSTRAINT ""FK_mk_lead_imports_users_uploaded_by"" FOREIGN KEY (uploaded_by_user_id) REFERENCES users (id) ON DELETE SET NULL
            );
            CREATE INDEX IF NOT EXISTS ""IX_mk_lead_imports_uploaded_at"" ON mk_lead_imports (uploaded_at);

            CREATE TABLE IF NOT EXISTS mk_activity (
                id uuid NOT NULL,
                ""text"" text NOT NULL,
                ""at"" timestamp with time zone NOT NULL,
                user_id integer,
                CONSTRAINT ""PK_mk_activity"" PRIMARY KEY (id),
                CONSTRAINT ""FK_mk_activity_users_user_id"" FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE SET NULL
            );
            CREATE INDEX IF NOT EXISTS ""IX_mk_activity_at"" ON mk_activity (""at"");
        ";

        /// <summary>
        /// Creates the mk_ tables. A failure is logged at Critical level and recorded in <see cref="MarketingState"/>, so every
        /// marketing endpoint answers 503 with the reason while the rest of the app keeps running.
        /// </summary>
        public static async Task<bool> InitializeAsync(MarketingDbContext db, MarketingState state, ILogger logger)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(Sql);
                state.SchemaReady = true;
                state.SchemaError = null;
                logger.LogInformation("Marketing schema is ready (mk_* tables).");
                return true;
            }
            catch (Exception ex)
            {
                state.SchemaReady = false;
                state.SchemaError = ex.Message;
                logger.LogCritical(ex, "Marketing schema initialization FAILED. /api/marketing/* will return 503 until it is fixed and the app restarted.");
                return false;
            }
        }
    }
}
