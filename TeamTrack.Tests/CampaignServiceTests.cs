using Microsoft.EntityFrameworkCore;
using TeamTrack.Workspace.Dtos;
using Xunit;
using static TeamTrack.Tests.Check;

namespace TeamTrack.Tests
{
    public class CampaignServiceTests
    {
        private static CampaignInput Full(string title = "Intro to Apollo") => new()
        {
            Title = title, ClientName = "Apollo Clinic", ContactName = "Dr. Rao", ClientEmail = "rao@apollo.example",
            Subject = "Faster medical coding", Body = "Hi Dr. Rao,\nLet's talk <soon>.",
        };

        private static async Task<CampaignDetailDto> DraftAsync(Kit k, string title = "Intro to Apollo")
            => Ok(await k.Campaigns.CreateAsync(k.Asha, Full(title)));

        // ---------- create / edit ----------

        [Fact]
        public async Task Employees_create_drafts_but_a_pm_cannot()
        {
            var k = new Kit();
            var c = await DraftAsync(k);
            Assert.Equal("draft", c.Status);
            Assert.Equal("Asha", c.CreatedByName);
            Assert.Equal(["Created"], c.History.Select(h => h.Action));

            Fails(await k.Campaigns.CreateAsync(k.Priya, Full()), 403);
            Fails(await k.Campaigns.CreateAsync(k.MeenaAsPm, Full()), 403); // "Both" acting as PM
            Ok(await k.Campaigns.CreateAsync(k.MeenaAsEmployee, Full()));
        }

        [Fact]
        public async Task A_draft_can_be_saved_with_only_a_title()
        {
            var k = new Kit();
            var c = Ok(await k.Campaigns.CreateAsync(k.Asha, new CampaignInput { Title = "Just an idea" }));
            Assert.Equal("", c.Subject);
            Fails(await k.Campaigns.CreateAsync(k.Asha, new CampaignInput { Title = " " }), 400, "title");
        }

        [Fact]
        public async Task Only_the_creator_can_edit_and_only_while_in_draft()
        {
            var k = new Kit();
            var c = await DraftAsync(k);
            Fails(await k.Campaigns.UpdateAsync(k.Ben, c.Id, Full("Hijack")), 403);
            var edited = Ok(await k.Campaigns.UpdateAsync(k.Asha, c.Id, Full("Renamed")));
            Assert.Equal("Renamed", edited.Title);

            Ok(await k.Campaigns.SendAsync(k.Asha, c.Id, true));
            Fails(await k.Campaigns.UpdateAsync(k.Asha, c.Id, Full("Too late")), 409);
        }

        // ---------- validation ----------

        [Theory]
        [InlineData("client")]
        [InlineData("contact")]
        [InlineData("email")]
        [InlineData("subject")]
        [InlineData("body")]
        public async Task Sending_needs_every_field(string missing)
        {
            var k = new Kit();
            var input = Full();
            switch (missing)
            {
                case "client": input.ClientName = ""; break;
                case "contact": input.ContactName = " "; break;
                case "email": input.ClientEmail = null; break;
                case "subject": input.Subject = ""; break;
                case "body": input.Body = ""; break;
            }
            var c = Ok(await k.Campaigns.CreateAsync(k.Asha, input));
            Fails(await k.Campaigns.SendAsync(k.Asha, c.Id, true), 400, "required");
            Assert.Equal("draft", Ok(await k.Campaigns.GetAsync(k.Asha, c.Id)).Status);
            Assert.Empty(k.Email.Sent);
        }

        [Theory]
        [InlineData("not-an-email")]
        [InlineData("a@b")]
        [InlineData("a@b.com, c@d.com")] // one recipient only
        [InlineData("Name <a@b.com>")]
        public async Task A_draft_needs_a_valid_email(string email)
        {
            var k = new Kit();
            var input = Full();
            input.ClientEmail = email;
            Fails(await k.Campaigns.CreateAsync(k.Asha, input), 400, "email");
        }

        [Fact]
        public async Task Single_line_fields_reject_line_breaks()
        {
            var k = new Kit();
            var input = Full();
            input.Subject = "Hello\r\nBcc: someone@else.example";
            Fails(await k.Campaigns.CreateAsync(k.Asha, input), 400, "single line");
        }

        [Fact]
        public async Task Placeholders_left_in_the_text_block_sending()
        {
            var k = new Kit();
            var input = Full();
            input.Body = "Hi,\nRegards,\n[Your name]";
            var c = Ok(await k.Campaigns.CreateAsync(k.Asha, input));
            Assert.Single(c.SendBlockers);
            Fails(await k.Campaigns.SendAsync(k.Asha, c.Id, true), 400, "[Your name]");
            Assert.Empty(k.Email.Sent);
        }

        // ---------- send ----------

        [Fact]
        public async Task Sending_emails_the_client_with_the_footer_and_marks_the_mail_sent()
        {
            var k = new Kit();
            var c = await DraftAsync(k);
            var sent = Ok(await k.Campaigns.SendAsync(k.Asha, c.Id, confirm: true));

            Assert.Equal("sent", sent.Status);
            Assert.NotNull(sent.SentAt);
            var mail = Assert.Single(k.Email.Sent);
            Assert.Equal("rao@apollo.example", mail.To);
            Assert.Equal("Faster medical coding", mail.Subject);
            Assert.Contains("12 Test Street, Chennai", mail.Body);
            Assert.Contains("Reply &#39;unsubscribe&#39; to stop these emails", mail.Body);
            Assert.Contains("Let&#39;s talk &lt;soon&gt;.", mail.Body); // body is HTML-encoded, never injected as markup
            Assert.Contains("<br/>", mail.Body);

            foreach (var pmId in new[] { k.Priya.Id, k.Ravi.Id, k.MeenaAsPm.Id })
                Assert.Contains("campaign_sent", await k.NotificationMessagesAsync(pmId));
            Assert.Empty(await k.NotificationMessagesAsync(k.Ben.Id));
        }

        [Fact]
        public async Task A_failed_send_keeps_the_mail_a_draft_and_reports_the_error()
        {
            var k = new Kit();
            var c = await DraftAsync(k);
            k.Email.FailWith = new InvalidOperationException("SMTP server unavailable");

            var failed = Fails(await k.Campaigns.SendAsync(k.Asha, c.Id, confirm: true), 502, "SMTP server unavailable");
            Assert.False(failed.Success);

            var after = Ok(await k.Campaigns.GetAsync(k.Asha, c.Id));
            Assert.Equal("draft", after.Status);
            Assert.Null(after.SentAt);
            Assert.DoesNotContain(after.History, h => h.Action == "Sent the mail");
            var failure = Assert.Single(after.History, h => h.Action == "Send failed"); // the error is recorded
            Assert.Contains("SMTP server unavailable", failure.Note);
            Assert.True(after.CanSend);

            k.Email.FailWith = null; // retry works
            Assert.Equal("sent", Ok(await k.Campaigns.SendAsync(k.Asha, c.Id, confirm: true)).Status);
        }

        [Fact]
        public async Task Send_needs_the_creator_a_draft_and_confirmation()
        {
            var k = new Kit();
            var c = await DraftAsync(k);
            Fails(await k.Campaigns.SendAsync(k.Priya, c.Id, true), 403);
            Fails(await k.Campaigns.SendAsync(k.Ben, c.Id, true), 403);
            Fails(await k.Campaigns.SendAsync(k.Asha, c.Id, false), 400, "confirm");
            Fails(await k.Campaigns.SendAsync(k.Asha, 999, true), 404);
            Assert.Empty(k.Email.Sent);

            Ok(await k.Campaigns.SendAsync(k.Asha, c.Id, true));
            Fails(await k.Campaigns.SendAsync(k.Asha, c.Id, true), 409);       // cannot be sent twice
            Assert.Single(k.Email.Sent);
        }

        [Fact]
        public async Task The_footer_falls_back_to_the_company_name_when_no_address_is_configured()
        {
            var k = new Kit(companyAddress: null);
            var c = await DraftAsync(k);
            Ok(await k.Campaigns.SendAsync(k.Asha, c.Id, true));
            Assert.Contains("Emedlogix Solutions", k.Email.Sent[0].Body);
            Assert.Contains("unsubscribe", k.Email.Sent[0].Body);
        }

        // ---------- history, list ----------

        [Fact]
        public async Task Every_step_is_recorded_in_the_history_in_order()
        {
            var k = new Kit();
            var c = await DraftAsync(k);
            var sent = Ok(await k.Campaigns.SendAsync(k.Asha, c.Id, true));
            Assert.Equal(["Created", "Sent the mail"], sent.History.Select(h => h.Action));
        }

        [Fact]
        public async Task Action_flags_depend_on_who_is_looking()
        {
            var k = new Kit();
            var c = await DraftAsync(k);

            var creator = Ok(await k.Campaigns.GetAsync(k.Asha, c.Id));
            Assert.True(creator.CanEdit && creator.CanSend);

            var other = Ok(await k.Campaigns.GetAsync(k.Ben, c.Id));
            Assert.False(other.CanEdit || other.CanSend);
            var pm = Ok(await k.Campaigns.GetAsync(k.Priya, c.Id));
            Assert.False(pm.CanEdit || pm.CanSend);

            Ok(await k.Campaigns.SendAsync(k.Asha, c.Id, true));
            var after = Ok(await k.Campaigns.GetAsync(k.Asha, c.Id));
            Assert.False(after.CanEdit || after.CanSend);
        }

        [Fact]
        public async Task The_mail_view_has_from_to_and_footer()
        {
            var k = new Kit();
            var c = await DraftAsync(k);
            Assert.Equal("Asha", c.FromName);
            Assert.Equal("asha@example.com", c.FromEmail);
            Assert.Equal("rao@apollo.example", c.ClientEmail);
            Assert.Equal("12 Test Street, Chennai\nReply 'unsubscribe' to stop these emails", c.Footer);
        }

        [Fact]
        public async Task List_counts_filters_by_status_and_searches_title_client_subject_and_creator()
        {
            var k = new Kit();
            await DraftAsync(k, "Alpha");
            var sentMail = await DraftAsync(k); // Intro to Apollo (Asha)
            Ok(await k.Campaigns.SendAsync(k.Asha, sentMail.Id, true));
            Ok(await k.Campaigns.CreateAsync(k.Ben, new CampaignInput { Title = "Beta", ClientName = "Zenith Hospital", Subject = "Hello zenith" }));

            var all = Ok(await k.Campaigns.ListAsync(k.Priya, null, false, null));
            Assert.Equal(3, all.Items.Count);
            Assert.Equal(2, all.Counts["draft"]);
            Assert.Equal(1, all.Counts["sent"]);

            var sentOnly = Ok(await k.Campaigns.ListAsync(k.Priya, "sent", false, null));
            Assert.Equal(sentMail.Id, Assert.Single(sentOnly.Items).Id);
            Assert.Equal(2, sentOnly.Counts["draft"]); // counts ignore the status filter

            Assert.Single(Ok(await k.Campaigns.ListAsync(k.Priya, null, false, "alpha")).Items);       // title
            Assert.Single(Ok(await k.Campaigns.ListAsync(k.Priya, null, false, "ZENITH")).Items);      // client / subject, any case
            Assert.Equal(2, Ok(await k.Campaigns.ListAsync(k.Priya, null, false, "asha")).Items.Count); // creator name
            Assert.Single(Ok(await k.Campaigns.ListAsync(k.Ben, null, true, null)).Items);              // only my mails

            Fails(await k.Campaigns.ListAsync(k.Priya, "bogus", false, null), 400);
        }

        [Fact]
        public async Task A_missing_campaign_is_404()
        {
            var k = new Kit();
            Fails(await k.Campaigns.GetAsync(k.Asha, 42), 404);
            Assert.Equal(0, await k.Ws.Campaigns.CountAsync());
        }
    }
}
