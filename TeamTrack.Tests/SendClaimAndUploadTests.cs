using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using TeamTrack.Services;
using TeamTrack.Workspace.Dtos;
using TeamTrack.Workspace.Services;
using Xunit;
using static TeamTrack.Tests.Check;

namespace TeamTrack.Tests
{
    /// <summary>An email service that stays "in flight" until the test releases it.</summary>
    public sealed class BlockingEmail : IEmailService
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Sent { get; } = [];
        public Exception? FailWith { get; set; }

        public Task Started => _started.Task;
        public void Release() => _release.TrySetResult();

        public async Task SendEmailAsync(string toEmail, string subject, string body)
        {
            _started.TrySetResult();
            await _release.Task;
            if (FailWith != null) throw FailWith;
            Sent.Add(toEmail);
        }
    }

    public class SendClaimTests
    {
        private static async Task<CampaignDetailDto> ApprovedAsync(Kit k)
        {
            var c = Ok(await k.Campaigns.CreateAsync(k.Asha, new CampaignInput
            { Title = "T", ClientName = "C", ContactName = "N", ClientEmail = "n@c.example", Subject = "S", Body = "B" }));
            Ok(await k.Campaigns.SubmitAsync(k.Asha, c.Id));
            return Ok(await k.Campaigns.ApproveAsync(k.Priya, c.Id));
        }

        private static async Task<string> StatusAsync(Kit k, int id)
        {
            await using var db = k.NewWorkspaceContext(); // fresh context: no tracked state
            return (await db.Campaigns.AsNoTracking().SingleAsync(c => c.Id == id)).Status;
        }

        [Fact]
        public async Task The_claim_is_a_conditional_update_that_only_one_caller_wins()
        {
            var k = new Kit();
            var c = await ApprovedAsync(k);
            var first = k.NewCampaignService(k.Email);
            var second = k.NewCampaignService(k.Email);

            Assert.True(await first.TryClaimAsync(c.Id));
            Assert.Equal("sending", await StatusAsync(k, c.Id));
            Assert.False(await second.TryClaimAsync(c.Id)); // nothing matched WHERE status = 'approved'
            Assert.False(await first.TryClaimAsync(c.Id));
            Assert.False(await first.TryClaimAsync(12345));
        }

        [Fact]
        public async Task A_second_send_while_the_first_is_in_flight_is_refused_and_the_client_gets_one_mail()
        {
            var k = new Kit();
            var c = await ApprovedAsync(k);
            var slow = new BlockingEmail();

            var firstRequest = k.NewCampaignService(slow).SendAsync(k.Asha, c.Id, confirm: true);
            await slow.Started.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("sending", await StatusAsync(k, c.Id)); // claimed before the mail was handed to SMTP

            var secondRequest = await k.NewCampaignService(k.Email).SendAsync(k.Asha, c.Id, confirm: true);
            Fails(secondRequest, 409, "already being sent");
            Assert.Empty(k.Email.Sent);

            slow.Release();
            var sent = Ok(await firstRequest);
            Assert.Equal("sent", sent.Status);
            Assert.Equal(["n@c.example"], slow.Sent);
            Assert.Equal("sent", await StatusAsync(k, c.Id));

            Fails(await k.NewCampaignService(k.Email).SendAsync(k.Asha, c.Id, true), 409); // and never again
            Assert.Empty(k.Email.Sent);
        }

        [Fact]
        public async Task The_creator_sees_the_mail_as_sending_and_cannot_act_on_it_meanwhile()
        {
            var k = new Kit();
            var c = await ApprovedAsync(k);
            Assert.True(await k.NewCampaignService(k.Email).TryClaimAsync(c.Id));
            var web = k.NewCampaignService(k.Email); // a fresh request: the kit's own context still has the old row cached

            var view = Ok(await web.GetAsync(k.Asha, c.Id));
            Assert.Equal("sending", view.Status);
            Assert.False(view.CanSend);
            Assert.False(view.CanEdit || view.CanTakeBack || view.CanApprove);
            Assert.Null(view.Badge);

            Fails(await web.SendAsync(k.Asha, c.Id, true), 409, "already being sent");
            Fails(await web.TakeBackAsync(k.Asha, c.Id), 409);
            Fails(await web.ApproveAsync(k.Ravi, c.Id), 409);
            Fails(await web.AskChangesAsync(k.Ravi, c.Id, "x"), 409);
            Fails(await web.UpdateAsync(k.Asha, c.Id, new CampaignInput { Title = "x" }), 409);

            var list = Ok(await web.ListAsync(k.Asha, "sending", false, null));
            Assert.Equal(c.Id, Assert.Single(list.Items).Id);
            Assert.Equal(1, list.Counts["sending"]);
            Assert.Equal(0, list.Counts["approved"]);
        }

        [Fact]
        public async Task A_failed_send_releases_the_claim_records_the_error_and_can_be_retried()
        {
            var k = new Kit();
            var c = await ApprovedAsync(k);
            var failing = new BlockingEmail { FailWith = new InvalidOperationException("mailbox full") };
            var request = k.NewCampaignService(failing).SendAsync(k.Asha, c.Id, true);
            await failing.Started.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("sending", await StatusAsync(k, c.Id));

            failing.Release();
            Fails(await request, 502, "mailbox full");
            Assert.Equal("approved", await StatusAsync(k, c.Id));

            var detail = Ok(await k.Campaigns.GetAsync(k.Asha, c.Id));
            Assert.Contains("mailbox full", Assert.Single(detail.History, h => h.Action == "Send failed").Note);
            Assert.True(detail.CanSend);

            Assert.Equal("sent", Ok(await k.Campaigns.SendAsync(k.Asha, c.Id, true)).Status); // retry works
            Assert.Single(k.Email.Sent);
        }

        [Fact]
        public async Task Validation_failures_do_not_claim_the_mail()
        {
            var k = new Kit();
            var c = await ApprovedAsync(k);
            Fails(await k.Campaigns.SendAsync(k.Asha, c.Id, confirm: false), 400, "confirm");
            Fails(await k.Campaigns.SendAsync(k.Ben, c.Id, true), 403);
            Assert.Equal("approved", await StatusAsync(k, c.Id));
        }
    }

    public class DocumentFileServiceTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ws-docs-" + Guid.NewGuid().ToString("N"));
        private readonly DocumentFileService _service;

        public DocumentFileServiceTests() => _service = new DocumentFileService(new FakeEnv(_root));

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private sealed class FakeEnv(string webRoot) : IWebHostEnvironment
        {
            public string WebRootPath { get; set; } = webRoot;
            public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
            public string ApplicationName { get; set; } = "t";
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
            public string ContentRootPath { get; set; } = webRoot;
            public string EnvironmentName { get; set; } = "Test";
        }

        private static IFormFile File(string name, byte[] bytes) => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);

        private static byte[] WithHeader(byte[] header, int total = 64)
        {
            var bytes = new byte[Math.Max(total, header.Length)];
            header.CopyTo(bytes, 0);
            return bytes;
        }

        private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7");
        private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];
        private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        private static readonly byte[] Jpg = [0xFF, 0xD8, 0xFF, 0xE0];

        [Theory]
        [InlineData("plan.pdf", "pdf")]
        [InlineData("budget.xlsx", "zip")]
        [InlineData("spec.docx", "zip")]
        [InlineData("deck.pptx", "zip")]
        [InlineData("logo.png", "png")]
        [InlineData("photo.jpg", "jpg")]
        [InlineData("photo.JPEG", "jpg")]
        [InlineData("SHOUT.PDF", "pdf")]
        public async Task Allowed_types_are_saved_under_uploads_documents_with_a_generated_name(string fileName, string kind)
        {
            var header = kind switch { "pdf" => Pdf, "zip" => Zip, "png" => Png, _ => Jpg };
            var content = WithHeader(header, 1000);

            var url = await _service.SaveAsync(File(fileName, content));

            Assert.StartsWith("/uploads/documents/", url);
            Assert.DoesNotContain(Path.GetFileNameWithoutExtension(fileName), url, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(Path.GetExtension(fileName).ToLowerInvariant(), url);
            var saved = Path.Combine(_root, "uploads", "documents", url["/uploads/documents/".Length..]);
            Assert.Equal(content, await System.IO.File.ReadAllBytesAsync(saved)); // the whole file, including the sniffed header
        }

        [Theory]
        [InlineData("run.exe")]
        [InlineData("page.html")]
        [InlineData("notes.txt")]
        [InlineData("archive.zip")]
        [InlineData("old.doc")]
        [InlineData("movie.mp4")]
        [InlineData("noextension")]
        public async Task Other_types_are_rejected(string fileName)
        {
            var ex = await Assert.ThrowsAsync<DocumentFileException>(() => _service.SaveAsync(File(fileName, WithHeader(Pdf))));
            Assert.Contains("allowed", ex.Message);
            Assert.False(Directory.Exists(Path.Combine(_root, "uploads", "documents")));
        }

        [Fact]
        public async Task Content_must_match_the_extension()
        {
            await Assert.ThrowsAsync<DocumentFileException>(() => _service.SaveAsync(File("fake.pdf", WithHeader(Png))));
            await Assert.ThrowsAsync<DocumentFileException>(() => _service.SaveAsync(File("fake.png", WithHeader(Pdf))));
            await Assert.ThrowsAsync<DocumentFileException>(() => _service.SaveAsync(File("fake.docx", Encoding.ASCII.GetBytes("MZ-not-a-zip-file"))));
            await Assert.ThrowsAsync<DocumentFileException>(() => _service.SaveAsync(File("tiny.pdf", [0x25])));
        }

        [Fact]
        public async Task Empty_files_and_files_over_20_MB_are_rejected_but_exactly_20_MB_is_fine()
        {
            await Assert.ThrowsAsync<DocumentFileException>(() => _service.SaveAsync(File("empty.pdf", [])));

            var over = await Assert.ThrowsAsync<DocumentFileException>(
                () => _service.SaveAsync(File("big.pdf", WithHeader(Pdf, (int)DocumentFileService.MaxBytes + 1))));
            Assert.Contains("20 MB", over.Message);

            var url = await _service.SaveAsync(File("max.pdf", WithHeader(Pdf, (int)DocumentFileService.MaxBytes)));
            Assert.StartsWith("/uploads/documents/", url);
        }

        [Fact]
        public async Task A_hostile_file_name_cannot_escape_the_documents_folder()
        {
            var url = await _service.SaveAsync(File("../../../evil.pdf", WithHeader(Pdf)));
            Assert.DoesNotContain("..", url);
            Assert.Single(Directory.GetFiles(Path.Combine(_root, "uploads", "documents")));
            Assert.Equal(DocumentFileService.MaxBytes, 20L * 1024 * 1024);
        }
    }
}
