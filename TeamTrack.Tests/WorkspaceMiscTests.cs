using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamTrack.Workspace;
using TeamTrack.Workspace.Data;
using TeamTrack.Workspace.Dtos;
using TeamTrack.Workspace.Models;
using TeamTrack.Workspace.Services;
using Xunit;
using static TeamTrack.Tests.Check;

namespace TeamTrack.Tests
{
    public class ProjectDocumentTests
    {
        private static DocumentForm Doc(string title = "Spec", string type = "doc", string? content = "hello", string? url = null, IFormFile? file = null)
            => new() { Title = title, DocType = type, Content = content, Url = url, File = file };

        private static IFormFile File(string name, string text = "data")
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);
        }

        [Fact]
        public async Task Members_can_add_and_view_documents()
        {
            var k = new Kit();
            var p = await k.AddProjectAsync("Alpha", k.Asha.Id, k.Ben.Id);

            var added = Ok(await k.Projects.AddDocumentAsync(k.Asha, p.Id, Doc()));
            Assert.Equal("Asha", added.UploadedByName);

            var list = Ok(await k.Projects.ListDocumentsAsync(k.Ben, p.Id)); // another member sees it: one shared library
            Assert.Equal(added.Id, Assert.Single(list).Id);
            Assert.Null(list[0].Content); // list is light, content comes from the single-document call
            Assert.Equal("hello", Ok(await k.Projects.GetDocumentAsync(k.Ben, p.Id, added.Id)).Content);
        }

        [Fact]
        public async Task Non_member_employees_cannot_view_or_add_documents()
        {
            var k = new Kit();
            var p = await k.AddProjectAsync("Alpha", k.Asha.Id);
            var doc = Ok(await k.Projects.AddDocumentAsync(k.Asha, p.Id, Doc()));

            foreach (var outsider in new[] { k.Ben, k.MeenaAsEmployee }) // "Both" user acting as Employee is not a PM
            {
                Fails(await k.Projects.ListDocumentsAsync(outsider, p.Id), 403, "members");
                Fails(await k.Projects.GetDocumentAsync(outsider, p.Id, doc.Id), 403);
                Fails(await k.Projects.AddDocumentAsync(outsider, p.Id, Doc("Sneaky")), 403);
                Fails(await k.Projects.GetAsync(outsider, p.Id), 403);
            }
            Assert.Equal(1, await k.Ws.ProjectDocuments.CountAsync());
            Fails(await k.Projects.ListDocumentsAsync(k.Asha, 999), 404);
        }

        [Fact]
        public async Task Product_managers_can_view_and_add_documents_in_every_project()
        {
            var k = new Kit();
            var p = await k.AddProjectAsync("Alpha", k.Asha.Id); // Priya and Meena are not members
            var doc = Ok(await k.Projects.AddDocumentAsync(k.Asha, p.Id, Doc("From Asha")));

            foreach (var pm in new[] { k.Priya, k.MeenaAsPm })
            {
                Assert.Equal(doc.Id, Assert.Single(Ok(await k.Projects.ListDocumentsAsync(pm, p.Id)), d => d.Title == "From Asha").Id);
                Assert.Equal("hello", Ok(await k.Projects.GetDocumentAsync(pm, p.Id, doc.Id)).Content);
                Assert.Equal(p.Id, Ok(await k.Projects.GetAsync(pm, p.Id)).Id);
            }
            var byPm = Ok(await k.Projects.AddDocumentAsync(k.Priya, p.Id, Doc("From Priya")));
            Assert.Equal("Priya", byPm.UploadedByName);
            Assert.Equal(2, Ok(await k.Projects.ListDocumentsAsync(k.Asha, p.Id)).Count); // members see the PM's document too
            Fails(await k.Projects.ListDocumentsAsync(k.Priya, 999), 404);
        }

        [Fact]
        public async Task The_same_person_loses_pm_access_when_acting_as_employee()
        {
            var k = new Kit();
            var p = await k.AddProjectAsync("Alpha", k.Asha.Id);
            Ok(await k.Projects.ListDocumentsAsync(k.MeenaAsPm, p.Id));
            Fails(await k.Projects.ListDocumentsAsync(k.MeenaAsEmployee, p.Id), 403);
        }

        [Fact]
        public async Task A_document_of_another_project_is_not_reachable_through_this_one()
        {
            var k = new Kit();
            var a = await k.AddProjectAsync("A", k.Asha.Id);
            var b = await k.AddProjectAsync("B", k.Ben.Id);
            var secret = Ok(await k.Projects.AddDocumentAsync(k.Ben, b.Id, Doc("B only")));
            Fails(await k.Projects.GetDocumentAsync(k.Asha, a.Id, secret.Id), 404);
        }

        [Fact]
        public async Task Document_fields_are_validated()
        {
            var k = new Kit();
            var p = await k.AddProjectAsync("Alpha", k.Asha.Id);

            Fails(await k.Projects.AddDocumentAsync(k.Asha, p.Id, Doc(title: " ")), 400, "title");
            Fails(await k.Projects.AddDocumentAsync(k.Asha, p.Id, Doc(type: "video")), 400, "type");
            Fails(await k.Projects.AddDocumentAsync(k.Asha, p.Id, Doc(content: null)), 400, "content");
            Fails(await k.Projects.AddDocumentAsync(k.Asha, p.Id, Doc(type: "link", content: null)), 400, "URL");
            Fails(await k.Projects.AddDocumentAsync(k.Asha, p.Id, Doc(type: "link", content: null, url: "javascript:alert(1)")), 400, "http");
            Fails(await k.Projects.AddDocumentAsync(k.Asha, p.Id, Doc(type: "link", content: null, url: "ftp://x.example/a")), 400, "http");

            Ok(await k.Projects.AddDocumentAsync(k.Asha, p.Id, Doc(type: "link", content: null, url: "https://docs.example/a")));
            Ok(await k.Projects.AddDocumentAsync(k.Asha, p.Id, Doc(type: "sheet", content: null, url: "https://sheets.example/b")));
            Ok(await k.Projects.AddDocumentAsync(k.Asha, p.Id, Doc(type: "slides", content: "outline")));
        }

        [Fact]
        public async Task A_file_is_stored_through_IFileService_and_its_errors_become_400()
        {
            var k = new Kit();
            var p = await k.AddProjectAsync("Alpha", k.Asha.Id);

            var ok = Ok(await k.Projects.AddDocumentAsync(k.Asha, p.Id, Doc(content: null, file: File("plan.pdf"))));
            Assert.Equal("/uploads/documents/plan.pdf", ok.FileUrl);

            Fails(await k.Projects.AddDocumentAsync(k.Asha, p.Id, Doc(content: null, file: File("bad.exe"))), 400, "allowed");
            Assert.Equal(1, await k.Ws.ProjectDocuments.CountAsync());
        }

        [Fact]
        public async Task Project_list_shows_progress_members_and_document_count_for_my_projects_only()
        {
            var k = new Kit();
            var mine = await k.AddProjectAsync("Mine", k.Asha.Id, k.Ben.Id);
            await k.AddProjectAsync("Theirs", k.Ben.Id);

            k.App.WorkItems.AddRange(
                new TeamTrack.Models.WorkItem { Title = "a", Status = "completed", ProjectId = mine.Id, CreatedByUserId = 1 },
                new TeamTrack.Models.WorkItem { Title = "b", Status = "released", ProjectId = mine.Id, CreatedByUserId = 1 },
                new TeamTrack.Models.WorkItem { Title = "c", Status = "in_progress", ProjectId = mine.Id, CreatedByUserId = 1 },
                new TeamTrack.Models.WorkItem { Title = "d", Status = "pending", ProjectId = mine.Id, CreatedByUserId = 1 },
                new TeamTrack.Models.WorkItem { Title = "bug", Status = "completed", WorkType = "Bug", ProjectId = mine.Id, CreatedByUserId = 1 });
            await k.App.SaveChangesAsync();
            Ok(await k.Projects.AddDocumentAsync(k.Asha, mine.Id, Doc()));

            var list = Ok(await k.Projects.ListAsync(k.Asha));
            var p = Assert.Single(list);
            Assert.Equal(["Mine", "Theirs"], Ok(await k.Projects.ListAsync(k.Priya)).Select(x => x.Name)); // a PM sees every project
            Assert.Empty(Ok(await k.Projects.ListAsync(k.Priya, mineOnly: true)));                      // ...unless asking for their own
            Assert.Equal((2, 4, 50, 1), (p.TasksDone, p.TasksTotal, p.ProgressPercent, p.DocumentCount)); // bugs excluded
            Assert.Equal(["Asha", "Ben"], p.Members.Select(m => m.Name));

            var detail = Ok(await k.Projects.GetAsync(k.Asha, mine.Id));
            Assert.Equal("Priya", detail.LeadName);
            Assert.Single(detail.Documents);
        }
    }

    public class PeopleHomeNotificationTests
    {
        [Fact]
        public async Task People_list_always_reports_working_even_for_someone_on_approved_leave_and_shows_project_tags()
        {
            // the leave backend stays in place but is not part of the people panel any more
            var k = new Kit();
            await k.AddProjectAsync("Alpha", k.Asha.Id);
            var leave = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, new LeaveRequestInput
            { LeaveType = "casual", FromDate = new(2026, 11, 3), ToDate = new(2026, 11, 5), Reason = "trip" }));
            Ok(await k.TimeOff.ApproveAsync(k.Priya, leave.Id));

            var people = Ok(await k.People.ListAsync(k.Ben));
            Assert.Equal(5, people.TotalCount);
            Assert.All(people.Items, p => { Assert.Equal("working", p.TodayStatus); Assert.Equal("Working", p.TodayLabel); });
            Assert.Equal(["Alpha"], people.Items.Single(p => p.Name == "Asha").Projects);
        }

        [Fact]
        public async Task Profile_has_no_leave_data_for_anyone()
        {
            var k = new Kit();
            Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, new LeaveRequestInput
            { LeaveType = "sick", FromDate = new(2026, 12, 1), ToDate = new(2026, 12, 2), Reason = "surgery" }));

            foreach (var viewer in new[] { k.Asha, k.Priya, k.Ben })
            {
                var profile = Ok(await k.People.ProfileAsync(viewer, k.Asha.Id));
                Assert.Equal("asha@example.com", profile.Email);
                Assert.Equal("Working", profile.TodayLabel);
                Assert.Null(profile.Balances);
                Assert.Empty(profile.UpcomingTimeOff);
            }
            Fails(await k.People.ProfileAsync(k.Ben, 999), 404);
        }

        [Fact]
        public async Task People_can_be_filtered_by_role_and_counted_per_role_tab()
        {
            var k = new Kit(); // Priya, Ravi = PM; Asha, Ben = Employee; Meena = Both

            var all = Ok(await k.People.ListAsync(k.Asha));
            Assert.Equal(5, all.TotalCount);
            Assert.Equal((5, 3, 3), (all.RoleCounts["all"], all.RoleCounts["Employee"], all.RoleCounts["ProductManager"])); // Meena counts in both

            var employees = Ok(await k.People.ListAsync(k.Asha, role: "Employee"));
            Assert.Equal(["Asha", "Ben", "Meena"], employees.Items.Select(p => p.Name));
            Assert.Equal(5, employees.TotalCount); // tab counts ignore the role filter
            var pms = Ok(await k.People.ListAsync(k.Asha, role: "ProductManager"));
            Assert.Equal(["Meena", "Priya", "Ravi"], pms.Items.Select(p => p.Name));

            Fails(await k.People.ListAsync(k.Asha, role: "Admin"), 400, "role");
        }

        [Fact]
        public async Task People_search_matches_name_or_role_and_tab_counts_follow_the_search()
        {
            var k = new Kit();
            Assert.Equal(["Ben"], Ok(await k.People.ListAsync(k.Asha, search: "BEN")).Items.Select(p => p.Name));

            var byRole = Ok(await k.People.ListAsync(k.Asha, search: "product manager"));
            Assert.Equal(["Meena", "Priya", "Ravi"], byRole.Items.Select(p => p.Name));
            Assert.Equal(3, byRole.RoleCounts["all"]);

            var both = Ok(await k.People.ListAsync(k.Asha, role: "Employee", search: "manager"));
            Assert.Equal(["Meena"], both.Items.Select(p => p.Name)); // only the "Both" user is an employee and matches "manager"
            Assert.Empty(Ok(await k.People.ListAsync(k.Asha, search: "nobody")).Items);
        }

        [Fact]
        public async Task Inactive_users_are_not_listed()
        {
            var k = new Kit();
            (await k.App.Users.FindAsync(4))!.IsActive = false;
            await k.App.SaveChangesAsync();
            var people = Ok(await k.People.ListAsync(k.Asha));
            Assert.DoesNotContain(people.Items, p => p.Name == "Ben");
            Fails(await k.People.ProfileAsync(k.Asha, 4), 404);
        }

        [Fact]
        public async Task Notifications_are_listed_newest_first_with_an_unread_count_and_marked_read_in_one_call()
        {
            var k = new Kit();
            k.Notifications.Queue([k.Asha.Id], "a", "first", "/x");
            await k.Ws.SaveChangesAsync();
            await Task.Delay(5);
            k.Notifications.Queue([k.Asha.Id], "b", "second", null);
            k.Notifications.Queue([k.Ben.Id], "c", "not yours", null);
            await k.Ws.SaveChangesAsync();

            var list = await k.Notifications.ListAsync(k.Asha.Id);
            Assert.Equal(2, list.UnreadCount);
            Assert.Equal(["second", "first"], list.Items.Select(n => n.Message));
            Assert.Equal(2, (await k.Notifications.ListAsync(k.Asha.Id)).UnreadCount); // listing alone does not mark read

            Assert.Equal(2, await k.Notifications.MarkAllReadAsync(k.Asha.Id));
            var after = await k.Notifications.ListAsync(k.Asha.Id);
            Assert.Equal(0, after.UnreadCount);
            Assert.All(after.Items, n => Assert.True(n.IsRead));
            Assert.Equal(1, (await k.Notifications.ListAsync(k.Ben.Id)).UnreadCount); // others untouched
        }

        [Fact]
        public async Task Pm_home_lists_pending_items_from_others_only()
        {
            var k = new Kit();
            var mail = Ok(await k.Campaigns.CreateAsync(k.Asha, new CampaignInput
            { Title = "T", ClientName = "C", ContactName = "N", ClientEmail = "n@c.example", Subject = "S", Body = "B" }));
            Ok(await k.Campaigns.SubmitAsync(k.Asha, mail.Id));
            Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, new LeaveRequestInput
            { LeaveType = "casual", FromDate = new(2026, 12, 1), ToDate = new(2026, 12, 1), Reason = "x" }));
            Ok(await k.TimeOff.RequestLeaveAsync(k.Priya, new LeaveRequestInput
            { LeaveType = "casual", FromDate = new(2026, 12, 8), ToDate = new(2026, 12, 8), Reason = "own" }));
            k.Ws.Campaigns.Add(new WsCampaign { Title = "Sent earlier", Status = CampaignStatus.Sent, CreatedByUserId = 3, SentAt = new DateTime(2026, 11, 2, 5, 0, 0, DateTimeKind.Utc) });
            k.Ws.Campaigns.Add(new WsCampaign { Title = "Sent last month", Status = CampaignStatus.Sent, CreatedByUserId = 3, SentAt = new DateTime(2026, 10, 30, 5, 0, 0, DateTimeKind.Utc) });
            await k.Ws.SaveChangesAsync();

            var home = Ok(await k.Home.GetAsync(k.Priya));
            Assert.Equal("ProductManager", home.Role);
            Assert.Equal(1, home.Tiles["mailsWaitingApproval"]);
            Assert.Equal(1, home.Tiles["timeOffWaiting"]); // Priya's own leave is for Ravi to decide
            Assert.Equal(1, home.Tiles["mailsSentThisMonth"]);
            Assert.Equal(0, home.Tiles["peopleAwayToday"]);
            Assert.Equal(2, home.Todo.Count);
            Assert.StartsWith("Approve mail:", home.Todo[0].Label);
            Assert.Equal($"/workspace/campaigns/{mail.Id}", home.Todo[0].Link);
            Assert.Equal("time_off", home.Todo[1].Kind);
        }

        [Fact]
        public async Task Employee_home_has_balance_tiles_and_ordered_todos()
        {
            var k = new Kit();
            CampaignInput Input(string title) => new() { Title = title, ClientName = "C", ContactName = "N", ClientEmail = "n@c.example", Subject = "S", Body = "B" };

            var plainDraft = Ok(await k.Campaigns.CreateAsync(k.Asha, Input("Plain draft")));
            var pending = Ok(await k.Campaigns.CreateAsync(k.Asha, Input("Pending one")));
            Ok(await k.Campaigns.SubmitAsync(k.Asha, pending.Id));
            var changes = Ok(await k.Campaigns.CreateAsync(k.Asha, Input("Needs fixing")));
            Ok(await k.Campaigns.SubmitAsync(k.Asha, changes.Id));
            Ok(await k.Campaigns.AskChangesAsync(k.Priya, changes.Id, "fix"));
            var approved = Ok(await k.Campaigns.CreateAsync(k.Asha, Input("Ready")));
            Ok(await k.Campaigns.SubmitAsync(k.Asha, approved.Id));
            Ok(await k.Campaigns.ApproveAsync(k.Priya, approved.Id));
            var leave = Ok(await k.TimeOff.RequestLeaveAsync(k.Asha, new LeaveRequestInput
            { LeaveType = "casual", FromDate = new(2026, 12, 1), ToDate = new(2026, 12, 2), Reason = "x" }));
            Ok(await k.TimeOff.ApproveAsync(k.Priya, leave.Id));
            var perm = Ok(await k.TimeOff.RequestPermissionAsync(k.Asha, new PermissionRequestInput
            { Date = new(2026, 11, 10), StartTime = "10:00", EndTime = "11:00", Reason = "x" }));

            var home = Ok(await k.Home.GetAsync(k.Asha));
            Assert.Equal("Employee", home.Role);
            Assert.Equal(10, home.Tiles["casualLeaveLeft"]);
            Assert.Equal(3, home.Tiles["permissionHoursLeft"]);
            Assert.Equal(1, home.Tiles["myWaitingApproval"]);
            Assert.Equal(0, home.Tiles["mySentThisMonth"]);
            Assert.Equal(
                ["Send mail: Ready", "Fix and resend: Needs fixing", "Finish draft: Plain draft", "Waiting for approval: Pending one"],
                home.Todo.Select(t => t.Label));
            Assert.NotEqual(0, perm.Id + plainDraft.Id);
        }
    }

    public class FeatureFlagTests
    {
        private static ResourceExecutingContext Context(IServiceProvider services)
            => new(new ActionContext(new DefaultHttpContext { RequestServices = services }, new RouteData(), new ActionDescriptor()),
                new List<IFilterMetadata>(), new List<IValueProviderFactory>());

        private static IConfiguration Config(string? value)
            => new ConfigurationBuilder().AddInMemoryCollection(value == null ? [] : new Dictionary<string, string?> { ["Features:Workspace"] = value }).Build();

        [Theory]
        [InlineData(null, false)]
        [InlineData("false", false)]
        [InlineData("true", true)]
        public void The_flag_defaults_to_off(string? value, bool expected)
            => Assert.Equal(expected, WorkspaceFeature.IsEnabled(Config(value)));

        [Fact]
        public void Env_var_style_key_turns_it_on()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Features__Workspace"] = "true" }).Build();
            // the env-var provider maps "__" to ":" before it reaches IConfiguration; emulate that mapping
            var mapped = new ConfigurationBuilder().AddInMemoryCollection(config.AsEnumerable().Select(kv => new KeyValuePair<string, string?>(kv.Key.Replace("__", ":"), kv.Value))).Build();
            Assert.True(WorkspaceFeature.IsEnabled(mapped));
        }

        [Fact]
        public void With_the_flag_off_nothing_is_registered_and_requests_get_404()
        {
            var services = new ServiceCollection().BuildServiceProvider(); // AddWorkspace was never called
            Assert.Null(services.GetService<ICampaignService>());
            Assert.Null(services.GetService<WorkspaceState>());

            var ctx = Context(services);
            new WorkspaceEnabledAttribute().OnResourceExecuting(ctx);
            var result = Assert.IsType<ObjectResult>(ctx.Result);
            Assert.Equal(404, result.StatusCode);
        }

        [Fact]
        public void AddWorkspace_registers_the_services_and_state()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWorkspace(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = "Host=x;Database=x" }).Build());
            foreach (var type in new[] { typeof(WorkspaceState), typeof(ICampaignService), typeof(ITimeOffService), typeof(IPeopleService),
                         typeof(IWorkspaceProjectService), typeof(IWorkspaceHomeService), typeof(INotificationService), typeof(WorkspaceDbContext) })
                Assert.Contains(services, d => d.ServiceType == type);
        }

        [Fact]
        public void A_failed_schema_gives_503_with_the_reason_and_a_ready_schema_passes()
        {
            var state = new WorkspaceState { SchemaReady = false, SchemaError = "connection refused" };
            var services = new ServiceCollection().AddSingleton(state).BuildServiceProvider();

            var failed = Context(services);
            new WorkspaceEnabledAttribute().OnResourceExecuting(failed);
            var result = Assert.IsType<ObjectResult>(failed.Result);
            Assert.Equal(503, result.StatusCode);
            Assert.Contains("connection refused", Assert.IsType<TeamTrack.DTOs.ApiResponse<string>>(result.Value).Message);

            state.SchemaReady = true;
            var ready = Context(services);
            new WorkspaceEnabledAttribute().OnResourceExecuting(ready);
            Assert.Null(ready.Result);
        }

        [Fact]
        public async Task Schema_initializer_failures_are_logged_as_critical_and_recorded_not_swallowed()
        {
            // The in-memory provider cannot run raw SQL, which makes ExecuteSqlRaw throw like a real DB failure.
            var db = new Kit().Ws;
            var state = new WorkspaceState();
            var logger = new CapturingLogger();

            await WorkspaceSchemaInitializer.InitializeAsync(db, state, logger);

            Assert.False(state.SchemaReady);
            Assert.False(string.IsNullOrWhiteSpace(state.SchemaError));
            var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
            Assert.Contains("Workspace schema initialization FAILED", entry.Message);
        }

        [Fact]
        public void The_schema_script_has_no_braces_because_ExecuteSqlRaw_formats_it()
        {
            Assert.DoesNotContain('{', WorkspaceSchemaInitializer.Sql);
            Assert.DoesNotContain('}', WorkspaceSchemaInitializer.Sql);
            _ = string.Format(WorkspaceSchemaInitializer.Sql); // what ExecuteSqlRaw does first; it must not throw
        }

        [Fact]
        public void Every_workspace_controller_is_gated_authorised_and_under_api_workspace()
        {
            var controllers = typeof(WorkspaceFeature).Assembly.GetTypes()
                .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && t.Namespace == "TeamTrack.Workspace.Controllers" && !t.IsAbstract)
                .ToList();
            Assert.Equal(7, controllers.Count);
            foreach (var c in controllers)
            {
                Assert.NotNull(Attribute.GetCustomAttribute(c, typeof(WorkspaceEnabledAttribute), inherit: true));
                Assert.NotNull(Attribute.GetCustomAttribute(c, typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true));
                Assert.StartsWith("api/workspace/", ((RouteAttribute)Attribute.GetCustomAttribute(c, typeof(RouteAttribute))!).Template);
            }
        }
    }
}
