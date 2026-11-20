using Microsoft.EntityFrameworkCore;
using TeamTrack.Data;
using TeamTrack.Marketing.Models;
using TeamTrack.Models;

namespace TeamTrack.Marketing.Data
{
    /// <summary>Development-only seed: the "Marketing Demo" user and exactly one demo lead. Safe to run more than once.</summary>
    public static class MarketingSeeder
    {
        public const string DemoEmail = "marketing.demo@example.com";
        public const string PasswordSetting = "Seed:MarketingDemoPassword";

        public static async Task SeedAsync(MarketingDbContext db, AppDbContext app, IConfiguration config, ILogger logger, DateTime nowUtc)
        {
            await SeedUserAsync(app, config, logger, nowUtc);
            await SeedLeadAsync(db, logger, nowUtc);
        }

        public static async Task SeedUserAsync(AppDbContext app, IConfiguration config, ILogger logger, DateTime nowUtc)
        {
            if (await app.Users.AnyAsync(u => u.Email.ToLower() == DemoEmail))
            {
                logger.LogInformation("Marketing demo user already exists; leaving it as it is.");
                return;
            }

            var password = config[PasswordSetting];
            if (string.IsNullOrWhiteSpace(password))
            {
                logger.LogWarning("{Setting} is not set, so the Marketing demo user was NOT created. Set it in user secrets or appsettings.Development.json.", PasswordSetting);
                return;
            }

            // Same columns as AuthService creates users with. UserType must be exactly "Marketing" so the role claim matches.
            app.Users.Add(new User
            {
                Name = "Marketing Demo",
                Email = DemoEmail,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                UserType = "Marketing",
                IsActive = true,
                CreatedAt = nowUtc,
            });
            await app.SaveChangesAsync();
            logger.LogInformation("Created the Marketing demo user ({Email}).", DemoEmail);
        }

        public static async Task SeedLeadAsync(MarketingDbContext db, ILogger logger, DateTime nowUtc)
        {
            if (await db.Leads.AnyAsync()) return;

            var lead = new MkLead
            {
                Id = Guid.NewGuid(), Name = "Sofia Marino", Title = "Managing Partner", Company = "Marino Legal",
                Email = "sofia.marino@example.com", Phone = null, Channel = "Webinar", Campaign = "Q4 webinar series",
                Interest = "Enterprise", Score = 88, Consent = true, Stage = "Replied",
                CreatedAt = nowUtc.AddDays(-13), UpdatedAt = nowUtc.AddDays(-10),
            };
            MkLeadHistoryEntry Entry(string kind, int daysAgo, string? subject, string? body, string? text) => new()
            {
                Id = Guid.NewGuid(), Kind = kind, OccurredAt = nowUtc.AddDays(-daysAgo), CreatedAt = nowUtc.AddDays(-daysAgo),
                Subject = subject, Body = body, Text = text,
            };
            lead.History.Add(Entry("Captured", 13, null, null, "Came in from webinar via “Q4 webinar series”."));
            lead.History.Add(Entry("Email sent", 12, "Webinar recording and slides",
                "Hi Sofia,\n\nThanks for joining the webinar. Here are the recording and slides.\n\nBest regards,\nMarketing team", null));
            lead.History.Add(Entry("Status", 10, null, null, "Contacted → Replied"));
            var reply = Entry("Reply received", 10, null, null, "Wants a case study from another law firm.");
            reply.CreatedAt = reply.CreatedAt.AddMilliseconds(1); // created after the Status line, so it sorts first (newest)
            lead.History.Add(reply);

            db.Leads.Add(lead);
            db.Activity.Add(new MkActivity { Id = Guid.NewGuid(), Text = "Sofia Marino captured from Q4 webinar series", At = nowUtc.AddDays(-13) });
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded the demo lead (Sofia Marino).");
        }
    }
}
