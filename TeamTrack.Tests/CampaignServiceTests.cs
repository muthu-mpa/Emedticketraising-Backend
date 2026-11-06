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

        private static async Task<CampaignDetailDto> PendingAsync(Kit k)
        {
            var c = await DraftAsync(k);
            return Ok(await k.Campaigns.SubmitAsync(k.Asha, c.Id));
        }

        private static async Task<CampaignDetailDto> ApprovedAsync(Kit k)
        {
            var c = await PendingAsync(k);
            return Ok(await k.Campaigns.ApproveAsync(k.Priya, c.Id));
        }

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

            Ok(await k.Campaigns.SubmitAsync(k.Asha, c.Id));
            Fails(await k.Campaigns.UpdateAsync(k.Asha, c.Id, Full("Too late")), 409);
        }

        // ---------- draft -> pending ----------

        [Theory]
        [InlineData("client")]
        [InlineData("contact")]
        [InlineData("email")]
        [InlineData("subject")]
        [InlineData("body")]
        public async Task Submitting_needs_every_field(string missing)
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
            Fails(await k.Campaigns.SubmitAsync(k.Asha, c.Id), 400, "required");
            Assert.Equal("draft", Ok(await k.Campaigns.GetAsync(k.Asha, c.Id)).Status);
        }

        [Theory]
        [InlineData("not-an-email")]
        [InlineData("a@b")]
        [InlineData("a@b.com, c@d.com")] // one recipient only
        [InlineData("Name <a@b.com>")]
        public async Task Submitting_needs_a_valid_email(string email)
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
        public async Task Only_the_creator_can_submit_and_only_a_draft()
        {
            var k = new Kit();
            var c = await DraftAsync(k);
            Fails(await k.Campaigns.SubmitAsync(k.Ben, c.Id), 403);
            Ok(await k.Campaigns.SubmitAsync(k.Asha, c.Id));
            Fails(await k.Campaigns.SubmitAsync(k.Asha, c.Id), 409);
        }

        [Fact]
        public async Task Submitting_notifies_every_pm_except_the_creator()
        {
            var k = new Kit();
            var c = await DraftAsync(k);
            Ok(await k.Campaigns.SubmitAsync(k.Asha, c.Id));

            Assert.Equal(["campaign_submitted"], await k.NotificationMessagesAsync(k.Priya.Id));
            Assert.Equal(["campaign_submitted"], await k.NotificationMessagesAsync(k.Ravi.Id));
            Assert.Equal(["campaign_submitted"], await k.NotificationMessagesAsync(k.MeenaAsPm.Id));
            Assert.Empty(await k.NotificationMessagesAsync(k.Asha.Id));
            Assert.Empty(await k.NotificationMessagesAsync(k.Ben.Id));
        }

        // ---------- approve / ask for changes / take back ----------

        [Fact]
        public async Task Only_a_pm_who_is_not_the_creator_can_approve()
        {
            var k = new Kit();
            var c = await PendingAsync(k);
            Fails(await k.Campaigns.ApproveAsync(k.Asha, c.Id), 403);          // the creator, an employee
            Fails(await k.Campaigns.ApproveAsync(k.Ben, c.Id), 403);           // another employee
            Fails(await k.Campaigns.ApproveAsync(k.MeenaAsEmployee, c.Id), 403);
            Ok(await k.Campaigns.ApproveAsync(k.Priya, c.Id));
            Assert.Equal(["campaign_approved"], await k.NotificationMessagesAsync(k.Asha.Id));
        }

        [Fact]
        public async Task A_creator_acting_as_pm_cannot_approve_or_review_their_own_mail()
        {
            var k = new Kit();
            var c = Ok(await k.Campaigns.CreateAsync(k.MeenaAsEmployee, Full()));
            Ok(await k.Campaigns.SubmitAsync(k.MeenaAsEmployee, c.Id));
            // Meena switches role to ProductManager: same user id, now PM
            Fails(await k.Campaigns.ApproveAsync(k.MeenaAsPm, c.Id), 403, "own");
            Fails(await k.Campaigns.AskChangesAsync(k.MeenaAsPm, c.Id, "note"), 403, "own");
        }

        [Fact]
        public async Task Approve_needs_a_pending_mail()
        {
            var k = new Kit();
            var c = await DraftAsync(k);
            Fails(await k.Campaigns.ApproveAsync(k.Priya, c.Id), 409);
            Fails(await k.Campaigns.ApproveAsync(k.Priya, 999), 404);
        }

        [Fact]
        public async Task Asking_for_changes_needs_a_note_and_sends_the_mail_back_to_draft()
        {
            var k = new Kit();
            var c = await PendingAsync(k);
            Fails(await k.Campaigns.AskChangesAsync(k.Priya, c.Id, "  "), 400, "what needs to change");
            Fails(await k.Campaigns.AskChangesAsync(k.Asha, c.Id, "x"), 403);

            var back = Ok(await k.Campaigns.AskChangesAsync(k.Priya, c.Id, "Mention pricing"));
            Assert.Equal("draft", back.Status);
            Assert.Equal("Mention pricing", back.ManagerNote);
            Assert.Equal("changes_asked", back.Badge);
            Assert.Contains("campaign_changes_asked", await k.NotificationMessagesAsync(k.Asha.Id));

            var asAsha = Ok(await k.Campaigns.GetAsync(k.Asha, c.Id));
            Assert.True(asAsha.CanEdit);
            Assert.Equal("Mention pricing", asAsha.ManagerNote);
        }

        [Fact]
        public async Task Resubmitting_clears_the_managers_note()
        {
            var k = new Kit();
            var c = await PendingAsync(k);
            Ok(await k.Campaigns.AskChangesAsync(k.Priya, c.Id, "Mention pricing"));
            var again = Ok(await k.Campaigns.SubmitAsync(k.Asha, c.Id));
            Assert.Equal("pending", again.Status);
            Assert.Null(again.ManagerNote);
        }

        [Fact]
        public async Task The_creator_can_take_a_pending_mail_back()
        {
            var k = new Kit();
            var c = await PendingAsync(k);
            Fails(await k.Campaigns.TakeBackAsync(k.Ben, c.Id), 403);
            Fails(await k.Campaigns.TakeBackAsync(k.Priya, c.Id), 403);
            var back = Ok(await k.Campaigns.TakeBackAsync(k.Asha, c.Id));
            Assert.Equal("draft", back.Status);
            Fails(await k.Campaigns.TakeBackAsync(k.Asha, c.Id), 409); // already a draft
        }

        // ---------- send ----------

        [Fact]
        public async Task Sending_emails_the_client_with_the_footer_and_marks_the_mail_sent()
        {
            var k = new Kit();
            var c = await ApprovedAsync(k);
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
        }

        [Fact]
        public async Task A_failed_send_keeps_the_mail_approved_and_reports_the_error()
        {
            var k = new Kit();
            var c = await ApprovedAsync(k);
            k.Email.FailWith = new InvalidOperationException("SMTP server unavailable");

            var failed = Fails(await k.Campaigns.SendAsync(k.Asha, c.Id, confirm: true), 502, "SMTP server unavailable");
            Assert.False(failed.Success);

            var after = Ok(await k.Campaigns.GetAsync(k.Asha, c.Id));
            Assert.Equal("approved", after.Status);
            Assert.Null(after.SentAt);
            Assert.DoesNotContain(after.History, h => h.Action == "Sent the mail");
            var failure = Assert.Single(after.History, h => h.Action == "Send failed"); // the error is recorded
            Assert.Contains("SMTP server unavailable", failure.Note);
            Assert.True(after.CanSend);

            k.Email.FailWith = null; // retry works
            Assert.Equal("sent", Ok(await k.Campaigns.SendAsync(k.Asha, c.Id, confirm: true)).Status);
        }

        [Fact]
        public async Task Send_needs_the_creator_an_approved_mail_and_confirmation()
        {
            var k = new Kit();
            var pending = await PendingAsync(k);
            Fails(await k.Campaigns.SendAsync(k.Asha, pending.Id, true), 409);       // not approved yet

            Ok(await k.Campaigns.ApproveAsync(k.Priya, pending.Id));
            Fails(await k.Campaigns.SendAsync(k.Priya, pending.Id, true), 403);      // approver is not the creator
            Fails(await k.Campaigns.SendAsync(k.Ben, pending.Id, true), 403);
            Fails(await k.Campaigns.SendAsync(k.Asha, pending.Id, false), 400, "confirm");
            Assert.Empty(k.Email.Sent);

            Ok(await k.Campaigns.SendAsync(k.Asha, pending.Id, true));
            Fails(await k.Campaigns.SendAsync(k.Asha, pending.Id, true), 409);       // cannot be sent twice
            Assert.Single(k.Email.Sent);
        }

        [Fact]
        public async Task The_footer_falls_back_to_the_company_name_when_no_address_is_configured()
        {
            var k = new Kit(companyAddress: null);
            var c = await ApprovedAsync(k);
            Ok(await k.Campaigns.SendAsync(k.Asha, c.Id, true));
            Assert.Contains("Emedlogix Solutions", k.Email.Sent[0].Body);
            Assert.Contains("unsubscribe", k.Email.Sent[0].Body);
        }

        // ---------- history, badges, list ----------

        [Fact]
        public async Task Every_step_is_recorded_in_the_history_in_order()
        {
            var k = new Kit();
            var c = await DraftAsync(k);
            Ok(await k.Campaigns.SubmitAsync(k.Asha, c.Id));
            Ok(await k.Campaigns.AskChangesAsync(k.Priya, c.Id, "Shorten it"));
            Ok(await k.Campaigns.SubmitAsync(k.Asha, c.Id));
            Ok(await k.Campaigns.TakeBackAsync(k.Asha, c.Id));
            Ok(await k.Campaigns.SubmitAsync(k.Asha, c.Id));
            Ok(await k.Campaigns.ApproveAsync(k.Ravi, c.Id));
            var sent = Ok(await k.Campaigns.SendAsync(k.Asha, c.Id, true));

            Assert.Equal(
                ["Created", "Sent for approval", "Asked for changes", "Sent for approval", "Took back to draft",
                 "Sent for approval", "Approved", "Sent the mail"],
                sent.History.Select(h => h.Action));
            Assert.Equal("Shorten it", sent.History[2].Note);
            Assert.Equal("Priya", sent.History[2].UserName);
            Assert.Equal("Ravi", sent.History[6].UserName);
        }

        [Fact]
        public async Task Badges_and_action_flags_depend_on_who_is_looking()
        {
            var k = new Kit();
            var c = await PendingAsync(k);

            var pm = Ok(await k.Campaigns.GetAsync(k.Priya, c.Id));
            Assert.Equal("needs_your_approval", pm.Badge);
            Assert.True(pm.CanApprove && pm.CanAskChanges);
            Assert.False(pm.CanTakeBack);

            var creator = Ok(await k.Campaigns.GetAsync(k.Asha, c.Id));
            Assert.Null(creator.Badge);
            Assert.True(creator.CanTakeBack);
            Assert.False(creator.CanApprove);

            Ok(await k.Campaigns.ApproveAsync(k.Priya, c.Id));
            Assert.Equal("ready_to_send", Ok(await k.Campaigns.GetAsync(k.Asha, c.Id)).Badge);
            Assert.Null(Ok(await k.Campaigns.GetAsync(k.Ben, c.Id)).Badge);
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
            var pending = await PendingAsync(k); // Intro to Apollo (Asha), pending
            Ok(await k.Campaigns.CreateAsync(k.Ben, new CampaignInput { Title = "Beta", ClientName = "Zenith Hospital", Subject = "Hello zenith" }));

            var all = Ok(await k.Campaigns.ListAsync(k.Priya, null, false, null));
            Assert.Equal(3, all.Items.Count);
            Assert.Equal(2, all.Counts["draft"]);
            Assert.Equal(1, all.Counts["pending"]);
            Assert.Equal(0, all.Counts["approved"]);
            Assert.Equal(0, all.Counts["sent"]);

            var pendingOnly = Ok(await k.Campaigns.ListAsync(k.Priya, "pending", false, null));
            Assert.Equal(pending.Id, Assert.Single(pendingOnly.Items).Id);
            Assert.Equal(2, pendingOnly.Counts["draft"]); // counts ignore the status filter

            Assert.Single(Ok(await k.Campaigns.ListAsync(k.Priya, null, false, "alpha")).Items);       // title
            Assert.Single(Ok(await k.Campaigns.ListAsync(k.Priya, null, false, "ZENITH")).Items);      // client / subject, any case
            Assert.Equal(2, Ok(await k.Campaigns.ListAsync(k.Priya, null, false, "asha")).Items.Count); // creator name
            Assert.Single(Ok(await k.Campaigns.ListAsync(k.Ben, null, true, null)).Items);              // only my mails

            Fails(await k.Campaigns.ListAsync(k.Priya, "bogus", false, null), 400);
        }

        [Fact]
        public void The_four_templates_are_available_with_placeholders()
        {
            var k = new Kit();
            var t = k.Campaigns.Templates();
            Assert.Equal(
                ["Faster medical coding for {client}", "Following up on my last mail", "A 20-minute demo for {client}", "Thank you for your time"],
                t.Select(x => x.Subject));
            Assert.All(t, x => Assert.Contains("{name}", x.Body));
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
