using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TeamTrack.Data;
using TeamTrack.Marketing.Data;
using TeamTrack.Marketing.Dtos;
using TeamTrack.Marketing.Models;
using TeamTrack.Marketing.Services;
using TeamTrack.Models;
using TeamTrack.Workspace.Services;
using Xunit;
using static TeamTrack.Tests.Check;

namespace TeamTrack.Tests
{
    public sealed class FakeMarketingFiles : IMarketingFileStore
    {
        public Dictionary<string, byte[]> Stored { get; } = [];
        public List<string> Deleted { get; } = [];

        public async Task<string> SaveAsync(Stream content, string extension)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms);
            var name = $"{Guid.NewGuid():N}{extension}";
            Stored[name] = ms.ToArray();
            return name;
        }

        public Stream? Open(string storedName) => Stored.TryGetValue(storedName, out var b) ? new MemoryStream(b) : null;
        public void Delete(string? storedName) { if (storedName != null) { Stored.Remove(storedName); Deleted.Add(storedName); } }
    }

    public sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>In-memory users, a SQLite marketing database and a fresh service (and context) per call, like separate web requests.</summary>
    public sealed class MarketingKit
    {
        public readonly CurrentUser Mara = new(1, "Mara", false);   // Marketing
        public readonly CurrentUser Noah = new(2, "Noah", false);   // Marketing
        public AppDbContext App { get; }
        public FakeMarketingFiles Files { get; } = new();
        public FakeTimeProvider Clock { get; } = new();
        private readonly SqliteConnection _connection;

        public MarketingKit()
        {
            App = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("app-" + Guid.NewGuid()).Options);
            App.Users.AddRange(
                new User { Id = 1, Name = "Mara", Email = "mara@example.com", UserType = "Marketing", PasswordHash = "x", IsActive = true },
                new User { Id = 2, Name = "Noah", Email = "noah@example.com", UserType = "Marketing", PasswordHash = "x", IsActive = true });
            App.SaveChanges();
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            using var db = NewContext();
            db.Database.EnsureCreated(); // the SQLite stand-in for MarketingSchemaInitializer
        }

        public MarketingDbContext NewContext()
            => new(new DbContextOptionsBuilder<MarketingDbContext>().UseSqlite(_connection).Options);

        public MarketingService Svc() => new(NewContext(), App, Files, Clock, NullLogger<MarketingService>.Instance);

        public async Task<Guid> AddLeadAsync(string name = "Sofia Marino", string stage = "New", int score = 50, int? owner = null,
            string channel = "Webinar", string campaign = "Q4 webinar series", DateTime? created = null)
        {
            await using var db = NewContext();
            var lead = new MkLead
            {
                Id = Guid.NewGuid(), Name = name, Title = "Partner", Company = name + " Co", Email = name.Replace(' ', '.').ToLower() + "@example.com",
                Channel = channel, Campaign = campaign, Interest = "Pro plan", Score = score, Stage = stage, Consent = true, OwnerUserId = owner,
                CreatedAt = created ?? Clock.Now.UtcDateTime, UpdatedAt = Clock.Now.UtcDateTime,
                CloseReason = stage == "Closed" ? "No response" : null,
            };
            db.Leads.Add(lead);
            await db.SaveChangesAsync();
            return lead.Id;
        }

        public static IncomingFile File(string name, byte[] bytes) => new(name, bytes.Length, () => new MemoryStream(bytes));
        public static IncomingFile BigFile(string name, long length) => new(name, length, () => new MemoryStream());

        public static HistoryInput Note(string text = "Spoke to the office manager.") => new() { Kind = "Note", Text = text };
        public static HistoryInput Email(string subject = "Hello") => new() { Kind = "Email sent", Subject = subject, Body = "Hi" };
    }

    public class MarketingServiceTests
    {
        // ---------- pick-up ----------

        [Fact]
        public async Task Pick_up_sets_the_owner_and_adds_a_history_entry()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync();
            var lead = Ok(await k.Svc().PickUpAsync(k.Mara, id));

            Assert.Equal("1", lead.OwnerUserId);
            Assert.Equal("Mara", lead.OwnerName);
            Assert.True(lead.IsMine);
            Assert.NotNull(lead.PickedUpAt);
            var entry = Assert.Single(lead.History);
            Assert.Equal("Picked up", entry.Kind);
            Assert.Equal("Moved to your work bench.", entry.Text);
            Assert.Empty(Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery())));
        }

        [Fact]
        public async Task A_lead_someone_else_got_first_is_a_409()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync();
            Ok(await k.Svc().PickUpAsync(k.Mara, id));
            Fails(await k.Svc().PickUpAsync(k.Noah, id), 409, "already been picked up");
            Fails(await k.Svc().PickUpAsync(k.Mara, id), 409, "already on your");
            Fails(await k.Svc().PickUpAsync(k.Mara, Guid.NewGuid()), 404);
        }

        [Fact]
        public async Task A_closed_lead_cannot_be_picked_up()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync(stage: "Closed");
            Fails(await k.Svc().PickUpAsync(k.Mara, id), 409);
        }

        // ---------- owner checks and visibility ----------

        [Fact]
        public async Task Only_the_owner_can_change_a_lead()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync();
            Ok(await k.Svc().PickUpAsync(k.Mara, id));

            Fails(await k.Svc().ReturnAsync(k.Noah, id), 403);
            Fails(await k.Svc().SetStageAsync(k.Noah, id, "Contacted"), 403);
            Fails(await k.Svc().SetScoreAsync(k.Noah, id, 10), 403);
            Fails(await k.Svc().CloseAsync(k.Noah, id, "Not interested"), 403);
            Fails(await k.Svc().AddHistoryAsync(k.Noah, id, MarketingKit.Note()), 403);
            Fails(await k.Svc().GetAsync(k.Noah, id), 403); // another user's lead is hidden too
        }

        [Fact]
        public async Task A_queue_lead_cannot_be_changed_before_it_is_picked_up()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync();
            Fails(await k.Svc().SetScoreAsync(k.Mara, id, 10), 403);
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, MarketingKit.Note()), 403);
            Ok(await k.Svc().GetAsync(k.Mara, id)); // but anyone in Marketing can look at it
        }

        [Fact]
        public async Task Return_clears_the_owner_and_puts_the_lead_back_in_the_queue()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync();
            Ok(await k.Svc().PickUpAsync(k.Mara, id));
            k.Clock.Now = k.Clock.Now.AddMinutes(5); // time passes between the two actions
            var back = Ok(await k.Svc().ReturnAsync(k.Mara, id));
            Assert.Null(back.OwnerUserId);
            Assert.False(back.IsMine);

            Assert.Single(Ok(await k.Svc().QueueAsync(k.Noah, new QueueQuery())));
            var detail = Ok(await k.Svc().GetAsync(k.Noah, id));
            Assert.Equal(["Returned", "Picked up"], detail.History.Select(h => h.Kind));
            Assert.Equal("Returned to the work queue.", detail.History[0].Text);
        }

        [Fact]
        public async Task A_closed_lead_cannot_be_returned()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync(stage: "Closed", owner: 1);
            Fails(await k.Svc().ReturnAsync(k.Mara, id), 409);
        }

        // ---------- stage ----------

        [Fact]
        public async Task A_stage_change_adds_a_status_entry_and_only_when_it_changes()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync(owner: 1);
            var lead = Ok(await k.Svc().SetStageAsync(k.Mara, id, "Interested"));
            Assert.Equal("Interested", lead.Stage);
            Assert.Equal("New → Interested", Assert.Single(lead.History, h => h.Kind == "Status").Text);

            Ok(await k.Svc().SetStageAsync(k.Mara, id, "Interested")); // same stage: nothing new
            Assert.Single(Ok(await k.Svc().GetAsync(k.Mara, id)).History);

            Fails(await k.Svc().SetStageAsync(k.Mara, id, "Bogus"), 400);
            Fails(await k.Svc().SetStageAsync(k.Mara, id, null), 400);
            Fails(await k.Svc().SetStageAsync(k.Mara, id, "Closed"), 400, "close action");
        }

        [Fact]
        public async Task Choosing_an_open_stage_on_a_closed_lead_reopens_it()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync(stage: "Closed", owner: 1);
            var lead = Ok(await k.Svc().SetStageAsync(k.Mara, id, "Contacted"));
            Assert.Equal("Contacted", lead.Stage);
            Assert.Null(lead.CloseReason);
            Assert.Equal("Closed → Contacted", lead.History[0].Text);
        }

        // ---------- score ----------

        [Fact]
        public async Task Score_is_clamped_and_a_change_is_logged()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync(score: 50, owner: 1);
            var high = Ok(await k.Svc().SetScoreAsync(k.Mara, id, 250));
            Assert.Equal(100, high.Score);
            Assert.Equal("Hot", high.Temperature);
            Assert.Equal("50 → 100", high.History[0].Text);

            var low = Ok(await k.Svc().SetScoreAsync(k.Mara, id, -5));
            Assert.Equal(0, low.Score);
            Assert.Equal("Cold", low.Temperature);

            Ok(await k.Svc().SetScoreAsync(k.Mara, id, 0)); // unchanged: no new entry
            Assert.Equal(2, Ok(await k.Svc().GetAsync(k.Mara, id)).History.Count(h => h.Kind == "Score"));
        }

        [Theory]
        [InlineData(70, "Hot")]
        [InlineData(69, "Warm")]
        [InlineData(40, "Warm")]
        [InlineData(39, "Cold")]
        public void Temperature_comes_from_the_score(int score, string expected)
            => Assert.Equal(expected, MarketingValues.TemperatureOf(score));

        // ---------- close ----------

        [Fact]
        public async Task Close_stores_the_reason_and_logs_it()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync(owner: 1);
            Fails(await k.Svc().CloseAsync(k.Mara, id, "Because"), 400);
            Fails(await k.Svc().CloseAsync(k.Mara, id, null), 400);

            var lead = Ok(await k.Svc().CloseAsync(k.Mara, id, "Not interested"));
            Assert.Equal("Closed", lead.Stage);
            Assert.Equal("Not interested", lead.CloseReason);
            Assert.Equal("Closed", lead.History[0].Kind);
            Assert.Equal("Not interested", lead.History[0].Text);
            Fails(await k.Svc().CloseAsync(k.Mara, id, "No response"), 409);
        }

        // ---------- logging history and automatic stage changes ----------

        [Fact]
        public async Task Logging_an_email_on_a_new_lead_moves_it_to_contacted()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync(owner: 1);
            var lead = Ok(await k.Svc().AddHistoryAsync(k.Mara, id, MarketingKit.Email("Intro")));
            Assert.Equal("Contacted", lead.Stage);
            Assert.Equal(["Status", "Email sent"], lead.History.Select(h => h.Kind));
            Assert.Equal("New → Contacted", lead.History[0].Text);
            Assert.Equal(1, lead.EmailsSent);
            Assert.NotNull(lead.LastContactAt);
            Assert.Equal("Mara", lead.History[1].CreatedByName);
        }

        [Fact]
        public async Task Logging_a_reply_moves_new_or_contacted_leads_to_replied_but_leaves_others()
        {
            var k = new MarketingKit();
            var fromNew = await k.AddLeadAsync("A One", owner: 1);
            var fromContacted = await k.AddLeadAsync("B Two", stage: "Contacted", owner: 1);
            var interested = await k.AddLeadAsync("C Three", stage: "Interested", owner: 1);

            var reply = new HistoryInput { Kind = "Reply received", Text = "Yes please" };
            Assert.Equal("Replied", Ok(await k.Svc().AddHistoryAsync(k.Mara, fromNew, reply)).Stage);
            Assert.Equal("Replied", Ok(await k.Svc().AddHistoryAsync(k.Mara, fromContacted, reply)).Stage);
            var stays = Ok(await k.Svc().AddHistoryAsync(k.Mara, interested, reply));
            Assert.Equal("Interested", stays.Stage);
            Assert.DoesNotContain(stays.History, h => h.Kind == "Status");
        }

        [Fact]
        public async Task An_email_on_a_contacted_lead_does_not_change_the_stage()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync(stage: "Contacted", owner: 1);
            var lead = Ok(await k.Svc().AddHistoryAsync(k.Mara, id, MarketingKit.Email()));
            Assert.Equal("Contacted", lead.Stage);
            Assert.DoesNotContain(lead.History, h => h.Kind == "Status");
        }

        [Fact]
        public async Task History_input_is_validated()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync(owner: 1);
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, new HistoryInput { Kind = "Captured", Text = "x" }), 400, "Kind");
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, new HistoryInput { Kind = null, Text = "x" }), 400);
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, new HistoryInput { Kind = "Email sent", Body = "no subject" }), 400, "subject");
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, new HistoryInput { Kind = "Note" }), 400, "text");
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, new HistoryInput { Kind = "Note", Text = "x", OccurredAt = "not a date" }), 400, "occurredAt");
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, new HistoryInput { Kind = "Note", Text = "x", OccurredAt = "2030-01-01T00:00:00Z" }), 400, "future");
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, new HistoryInput { Kind = "Email sent", Subject = "a\r\nBcc: x@y.z" }), 400, "single line");
            Assert.Empty(Ok(await k.Svc().GetAsync(k.Mara, id)).History);
        }

        [Fact]
        public async Task A_logged_time_may_be_at_most_five_minutes_ahead_of_the_server_clock()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync(owner: 1);
            HistoryInput At(string iso) => new() { Kind = "Note", Text = "x", OccurredAt = iso };

            Ok(await k.Svc().AddHistoryAsync(k.Mara, id, At("2026-11-18T10:04:00Z")));          // +4 min: fine
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, At("2026-11-18T10:06:00Z")), 400, "future");   // +6 min
            Ok(await k.Svc().AddHistoryAsync(k.Mara, id, At("2026-11-18T15:30:00+05:30")));      // 10:00 UTC, compared in UTC
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, At("2026-11-18T16:00:00+05:30")), 400, "future"); // 10:30 UTC
        }

        [Fact]
        public async Task A_file_alone_is_enough_for_a_note_and_the_occurred_at_is_kept()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync(owner: 1);
            var input = new HistoryInput
            {
                Kind = "Note", OccurredAt = "2026-11-10T08:30:00Z",
                Files = [MarketingKit.File("notes.txt", "hello"u8.ToArray())],
            };
            var lead = Ok(await k.Svc().AddHistoryAsync(k.Mara, id, input));
            Assert.Equal(new DateTime(2026, 11, 10, 8, 30, 0, DateTimeKind.Utc), lead.History[0].OccurredAt);
            Assert.Single(lead.History[0].Attachments);
        }

        // ---------- uploads ----------

        [Fact]
        public async Task Files_are_stored_under_a_generated_name_with_signed_details()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync(owner: 1);
            var input = new HistoryInput
            {
                Kind = "Note", Text = "Contract back",
                Files = [MarketingKit.File("..\\..\\x-signed.PDF", [1, 2, 3]), MarketingKit.File("photo.png", [9, 9])],
                FilesMeta = """[{"fileName":"x-signed.pdf","signedBy":"Priya Raman","signedAt":"2026-11-17T09:00:00Z"},{"fileName":"photo.png","signedBy":null,"signedAt":null}]""",
            };
            var lead = Ok(await k.Svc().AddHistoryAsync(k.Mara, id, input));
            var files = lead.History[0].Attachments;
            Assert.Equal(2, files.Count);

            var signed = Assert.Single(files, f => f.SignedBy != null);
            Assert.Equal("x-signed.PDF", signed.FileName);              // the path is stripped from the shown name
            Assert.Equal("application/pdf", signed.ContentType);        // from the extension, not from the client
            Assert.Equal(3, signed.Size);
            Assert.Equal(new DateTime(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc), signed.SignedAt);
            Assert.Null(Assert.Single(files, f => f.FileName == "photo.png").SignedBy);

            Assert.Equal(2, k.Files.Stored.Count);
            Assert.All(k.Files.Stored.Keys, name => Assert.DoesNotContain("signed", name)); // never the user's file name
        }

        [Fact]
        public async Task Upload_limits_are_enforced_and_nothing_is_saved_on_failure()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync(owner: 1);
            HistoryInput With(params IncomingFile[] files) => new() { Kind = "Note", Text = "x", Files = files.ToList() };

            var six = Enumerable.Range(0, 6).Select(i => MarketingKit.File($"f{i}.txt", [1])).ToArray();
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, With(six)), 400, "At most 5");

            var five = Enumerable.Range(0, 5).Select(i => MarketingKit.File($"f{i}.txt", [1])).ToArray();
            Ok(await k.Svc().AddHistoryAsync(k.Mara, id, With(five)));

            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, With(MarketingKit.BigFile("big.pdf", MarketingService.MaxFileBytes + 1))), 400, "too large");
            Ok(await k.Svc().AddHistoryAsync(k.Mara, id, With(MarketingKit.BigFile("max.pdf", MarketingService.MaxFileBytes))));
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, With(MarketingKit.File("run.exe", [1]))), 400, "not an allowed");
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, With(MarketingKit.File("noext", [1]))), 400, "not an allowed");
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, With(MarketingKit.File("empty.txt", []))), 400, "empty");

            var one = With(MarketingKit.File("a.txt", [1]));
            one.FilesMeta = "[]";
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, one), 400, "one entry per file");
            one.FilesMeta = "{not json";
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, one), 400, "JSON");
            one.FilesMeta = """[{"signedAt":"nope"}]""";
            Fails(await k.Svc().AddHistoryAsync(k.Mara, id, one), 400, "signedAt");

            Assert.Equal(6, k.Files.Stored.Count); // only the five accepted files and the 10 MB one
        }

        // ---------- attachments ----------

        [Fact]
        public async Task An_attachment_can_only_be_opened_by_someone_who_can_see_the_lead()
        {
            var k = new MarketingKit();
            var id = await k.AddLeadAsync(owner: 1);
            var lead = Ok(await k.Svc().AddHistoryAsync(k.Mara, id,
                new HistoryInput { Kind = "Note", Text = "x", Files = [MarketingKit.File("deal.pdf", [7, 7, 7])] }));
            var attachmentId = lead.History[0].Attachments[0].Id;

            var file = Ok(await k.Svc().GetAttachmentAsync(k.Mara, attachmentId));
            Assert.Equal("deal.pdf", file.FileName);
            Assert.Equal("application/pdf", file.ContentType);
            using var ms = new MemoryStream();
            await file.Content.CopyToAsync(ms);
            Assert.Equal([7, 7, 7], ms.ToArray());

            Fails(await k.Svc().GetAttachmentAsync(k.Noah, attachmentId), 403);   // another user's lead
            Fails(await k.Svc().GetAttachmentAsync(k.Mara, Guid.NewGuid()), 404);

            Ok(await k.Svc().ReturnAsync(k.Mara, id));                            // back in the queue: visible to the team
            Ok(await k.Svc().GetAttachmentAsync(k.Noah, attachmentId));
        }

        // ---------- queue and bench ----------

        [Fact]
        public async Task The_queue_holds_only_open_unowned_leads_with_filters_and_sorting()
        {
            var k = new MarketingKit();
            await k.AddLeadAsync("Zed Hot", score: 90, channel: "Event", campaign: "Newsletter");
            await k.AddLeadAsync("Amy Warm", score: 50, stage: "Contacted");
            await k.AddLeadAsync("Bob Cold", score: 10);
            await k.AddLeadAsync("Owned One", owner: 2);
            await k.AddLeadAsync("Closed One", stage: "Closed");

            var all = Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery()));
            Assert.Equal(["Zed Hot", "Amy Warm", "Bob Cold"], all.Select(l => l.Name)); // default: score, high first

            Assert.Equal(["Zed Hot", "Bob Cold", "Amy Warm"], Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery { Sort = "name", Dir = "desc" })).Select(l => l.Name));
            Assert.Equal(["Amy Warm", "Bob Cold", "Zed Hot"], Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery { Sort = "name", Dir = "asc" })).Select(l => l.Name));
            Assert.Equal("Zed Hot", Assert.Single(Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery { Temperature = "Hot" }))).Name);
            Assert.Equal("Amy Warm", Assert.Single(Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery { Stage = "Contacted" }))).Name);
            Assert.Equal("Zed Hot", Assert.Single(Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery { Channel = "Event" }))).Name);
            Assert.Equal("Zed Hot", Assert.Single(Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery { Campaign = "Newsletter" }))).Name);
            Assert.Equal("Bob Cold", Assert.Single(Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery { Search = "BOB" }))).Name);
            Assert.Empty(Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery { Stage = "Closed" })));

            Fails(await k.Svc().QueueAsync(k.Mara, new QueueQuery { Sort = "bogus" }), 400);
            Fails(await k.Svc().QueueAsync(k.Mara, new QueueQuery { Dir = "sideways" }), 400);
            Fails(await k.Svc().QueueAsync(k.Mara, new QueueQuery { Channel = "Carrier pigeon" }), 400);
        }

        [Fact]
        public async Task The_queue_can_be_sorted_by_last_contact_and_emails()
        {
            var k = new MarketingKit();
            var quiet = await k.AddLeadAsync("Quiet Lead", owner: 1);
            var busy = await k.AddLeadAsync("Busy Lead", owner: 1);
            Ok(await k.Svc().AddHistoryAsync(k.Mara, busy, MarketingKit.Email("one")));
            Ok(await k.Svc().AddHistoryAsync(k.Mara, busy, MarketingKit.Email("two")));
            Ok(await k.Svc().ReturnAsync(k.Mara, quiet));
            Ok(await k.Svc().ReturnAsync(k.Mara, busy));

            var byEmails = Ok(await k.Svc().QueueAsync(k.Noah, new QueueQuery { Sort = "emails", Dir = "desc" }));
            Assert.Equal(["Busy Lead", "Quiet Lead"], byEmails.Select(l => l.Name));
            Assert.Equal([2, 0], byEmails.Select(l => l.EmailsSent));
            var byContact = Ok(await k.Svc().QueueAsync(k.Noah, new QueueQuery { Sort = "lastContact", Dir = "desc" }));
            Assert.Equal("Busy Lead", byContact[0].Name);
            Assert.Null(byContact[1].LastContactAt);
        }

        [Fact]
        public async Task The_bench_lists_only_my_leads_by_status()
        {
            var k = new MarketingKit();
            await k.AddLeadAsync("Mine Open", owner: 1);
            await k.AddLeadAsync("Mine Closed", stage: "Closed", owner: 1);
            await k.AddLeadAsync("Theirs", owner: 2);
            await k.AddLeadAsync("Queue");

            Assert.Equal(["Mine Open"], Ok(await k.Svc().BenchAsync(k.Mara, null)).Select(l => l.Name));
            Assert.Equal(["Mine Closed"], Ok(await k.Svc().BenchAsync(k.Mara, "closed")).Select(l => l.Name));
            Assert.Equal(2, Ok(await k.Svc().BenchAsync(k.Mara, "all")).Count);
            Assert.All(Ok(await k.Svc().BenchAsync(k.Mara, "all")), l => Assert.True(l.IsMine));
            Fails(await k.Svc().BenchAsync(k.Mara, "bogus"), 400);
        }

        // ---------- dashboard ----------

        [Fact]
        public async Task The_dashboard_is_team_wide_except_my_bench()
        {
            var k = new MarketingKit();
            var mine = await k.AddLeadAsync("Mine", owner: 1, score: 80, channel: "Event", campaign: "Newsletter");
            await k.AddLeadAsync("Theirs", owner: 2, score: 20);
            await k.AddLeadAsync("Waiting", score: 45);
            await k.AddLeadAsync("Done", stage: "Closed", owner: 2);
            Ok(await k.Svc().AddHistoryAsync(k.Mara, mine, MarketingKit.Email("Intro")));
            Ok(await k.Svc().AddHistoryAsync(k.Mara, mine, new HistoryInput { Kind = "Reply received", Text = "Thanks" }));

            var mara = Ok(await k.Svc().DashboardAsync(k.Mara));
            Assert.Equal(4, mara.LeadsTracked);
            Assert.Equal(1, mara.EmailsSent);
            Assert.Equal(1, mara.WaitingInQueue);
            Assert.Equal(1, mara.OnMyBench);
            Assert.Equal(1.0, mara.ReplyRate);
            Assert.Equal(1, mara.ByStage.Single(s => s.Stage == "Replied").Count);
            Assert.Equal(1, mara.ByStage.Single(s => s.Stage == "Closed").Count);
            Assert.Equal(1, mara.ByChannel.Single(c => c.Channel == "Event").Count);
            Assert.Equal(1, mara.ByTemperature.Single(t => t.Temperature == "Hot").Count);
            var newsletter = mara.ByCampaign.Single(c => c.Campaign == "Newsletter");
            Assert.Equal((1, 1), (newsletter.Leads, newsletter.Emails));
            Assert.Contains(mara.RecentActivity, a => a.Text == "Email to Mine: Intro");

            var noah = Ok(await k.Svc().DashboardAsync(k.Noah));
            Assert.Equal(1, noah.OnMyBench);          // "Theirs" is open; "Done" is closed
            Assert.Equal(4, noah.LeadsTracked);       // the rest is team-wide
        }

        [Fact]
        public async Task The_reply_rate_is_zero_when_no_emails_were_sent()
        {
            var k = new MarketingKit();
            await k.AddLeadAsync();
            Assert.Equal(0.0, Ok(await k.Svc().DashboardAsync(k.Mara)).ReplyRate);
        }

        [Fact]
        public void Lookups_serve_the_fixed_lists()
        {
            var l = new MarketingKit().Svc().Lookups();
            Assert.Equal(new[] { "New", "Contacted", "Replied", "Interested", "Closed" }, l.Stages);
            Assert.Equal(new[] { "Email sent", "Reply received", "Call", "Note" }, l.LoggableKinds);
            Assert.Contains("Buyer’s guide ebook", l.Campaigns);
            Assert.Equal(10, l.HistoryKinds.Length);
        }
    }

    public class MarketingSeederTests
    {
        private static AppDbContext NewApp() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("app-" + Guid.NewGuid()).Options);
        private static IConfiguration Config(string? password)
            => new ConfigurationBuilder().AddInMemoryCollection(password == null ? [] : new Dictionary<string, string?> { [MarketingSeeder.PasswordSetting] = password }).Build();

        [Fact]
        public async Task The_demo_lead_is_seeded_once_with_its_four_history_entries()
        {
            var k = new MarketingKit();
            var now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
            await using (var db = k.NewContext())
            {
                await MarketingSeeder.SeedLeadAsync(db, NullLogger.Instance, now);
                await MarketingSeeder.SeedLeadAsync(db, NullLogger.Instance, now); // safe to run twice
            }
            var queue = Ok(await k.Svc().QueueAsync(k.Mara, new QueueQuery()));
            var sofia = Assert.Single(queue);
            Assert.Equal("Sofia Marino", sofia.Name);
            Assert.Equal(88, sofia.Score);
            Assert.Equal("Hot", sofia.Temperature);
            Assert.Equal("Replied", sofia.Stage);
            Assert.Null(sofia.OwnerUserId);
            Assert.Equal(1, sofia.EmailsSent);

            var detail = Ok(await k.Svc().GetAsync(k.Mara, sofia.Id));
            Assert.Equal(["Reply received", "Status", "Email sent", "Captured"], detail.History.Select(h => h.Kind));
            Assert.Equal("Came in from webinar via “Q4 webinar series”.", detail.History[3].Text);
            Assert.Equal("Contacted → Replied", detail.History[1].Text);
            Assert.Equal("Webinar recording and slides", detail.History[2].Subject);
            Assert.Equal("Q4 webinar series", detail.Campaign);

            var dash = Ok(await k.Svc().DashboardAsync(k.Mara));
            Assert.Equal("Sofia Marino captured from Q4 webinar series", Assert.Single(dash.RecentActivity).Text);
        }

        [Fact]
        public async Task The_demo_user_needs_a_configured_password_and_is_created_once()
        {
            var app = NewApp();
            var log = new CapturingLogger();
            await MarketingSeeder.SeedUserAsync(app, Config(null), log, DateTime.UtcNow);
            Assert.Empty(app.Users);
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(MarketingSeeder.PasswordSetting));

            await MarketingSeeder.SeedUserAsync(app, Config("S3cret-demo!"), log, DateTime.UtcNow);
            await MarketingSeeder.SeedUserAsync(app, Config("another"), log, DateTime.UtcNow);
            var user = Assert.Single(app.Users);
            Assert.Equal("Marketing Demo", user.Name);
            Assert.Equal("marketing.demo@example.com", user.Email);
            Assert.Equal("Marketing", user.UserType);
            Assert.True(user.IsActive);
            Assert.True(BCrypt.Net.BCrypt.Verify("S3cret-demo!", user.PasswordHash)); // the first password wins; never plain text
        }
    }
}
