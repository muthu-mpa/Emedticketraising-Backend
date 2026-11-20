using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TeamTrack.Data;
using TeamTrack.Models;
using TeamTrack.Services;
using TeamTrack.Workspace.Data;
using TeamTrack.Workspace.Services;
using Xunit;

namespace TeamTrack.Tests
{
    public sealed class FakeClock : IWorkspaceClock
    {
        public DateOnly Today { get; set; } = new(2026, 11, 4);
        public DateTime UtcNow => DateTime.UtcNow;

        public (DateTime StartUtc, DateTime EndUtc) MonthRangeUtc(DateOnly day)
        {
            var start = new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            return (start, start.AddMonths(1));
        }
    }

    public sealed class FakeEmail : IEmailService
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = [];
        public Exception? FailWith { get; set; }

        public Task SendEmailAsync(string toEmail, string subject, string body)
        {
            if (FailWith != null) throw FailWith;
            Sent.Add((toEmail, subject, body));
            return Task.CompletedTask;
        }
    }

    public sealed class FakeFiles : IDocumentFileService
    {
        public Task<string> SaveAsync(IFormFile file)
        {
            if (file.FileName.EndsWith(".exe")) throw new DocumentFileException("Only pdf, docx, xlsx, pptx, png and jpg files are allowed.");
            return Task.FromResult($"/uploads/documents/{file.FileName}");
        }
    }

    public sealed class FakeSheetFiles : ISheetFileStore
    {
        public Dictionary<string, byte[]> Stored { get; } = [];
        public List<string> Deleted { get; } = [];

        public Task<string> SaveAsync(byte[] bytes)
        {
            var path = $"/uploads/sheets/{Guid.NewGuid():N}.xlsx";
            Stored[path] = bytes;
            return Task.FromResult(path);
        }

        public void Delete(string? path)
        {
            if (path == null) return;
            Stored.Remove(path);
            Deleted.Add(path);
        }
    }

    public sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>In-memory users/projects, a SQLite (relational) workspace database, seeded users, and the services under test.</summary>
    public sealed class Kit
    {
        public readonly CurrentUser Priya = new(1, "Priya", true);   // ProductManager
        public readonly CurrentUser Ravi = new(2, "Ravi", true);     // ProductManager
        public readonly CurrentUser Asha = new(3, "Asha", false);    // Employee
        public readonly CurrentUser Ben = new(4, "Ben", false);      // Employee
        public readonly CurrentUser MeenaAsEmployee = new(5, "Meena", false); // "Both" user
        public readonly CurrentUser MeenaAsPm = new(5, "Meena", true);

        public AppDbContext App { get; }
        public WorkspaceDbContext Ws { get; }
        private readonly SqliteConnection _wsConnection;
        private readonly IConfiguration _config;
        public FakeClock Clock { get; } = new();
        public FakeEmail Email { get; } = new();
        public FakeFiles Files { get; } = new();
        public FakeSheetFiles SheetFiles { get; } = new();

        public INotificationService Notifications { get; }
        public ICampaignService Campaigns { get; }
        public ITimeOffService TimeOff { get; }
        public IWorkspaceProjectService Projects { get; }
        public IPeopleService People { get; }
        public IWorkspaceHomeService Home { get; }
        public ISharedSheetService Sheets { get; }

        public Kit(string? companyAddress = "12 Test Street, Chennai")
        {
            var name = Guid.NewGuid().ToString();
            App = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("app-" + name).Options);
            // SQLite, not the in-memory provider: the send claim is a conditional UPDATE (ExecuteUpdate)
            _wsConnection = new SqliteConnection("DataSource=:memory:");
            _wsConnection.Open();
            Ws = NewWorkspaceContext();
            Ws.Database.EnsureCreated();

            App.Users.AddRange(
                U(1, "Priya", "ProductManager"), U(2, "Ravi", "ProductManager"),
                U(3, "Asha", "Employee"), U(4, "Ben", "Employee"), U(5, "Meena", "Both"));
            App.SaveChanges();

            _config = new ConfigurationBuilder()
                .AddInMemoryCollection(companyAddress == null ? [] : new Dictionary<string, string?> { ["Workspace:CompanyAddress"] = companyAddress })
                .Build();

            Notifications = new NotificationService(Ws, App);
            TimeOff = new TimeOffService(Ws, App, Notifications, Clock);
            Campaigns = new CampaignService(Ws, App, Notifications, Email, _config, NullLogger<CampaignService>.Instance);
            Projects = new WorkspaceProjectService(Ws, App, Files);
            People = new PeopleService(App);
            Home = new WorkspaceHomeService(Ws, App, TimeOff, Projects, Clock);
            Sheets = new SharedSheetService(Ws, App, Notifications, SheetFiles, NullLogger<SharedSheetService>.Instance);
        }

        public WorkspaceDbContext NewWorkspaceContext()
            => new(new DbContextOptionsBuilder<WorkspaceDbContext>().UseSqlite(_wsConnection).Options);

        /// <summary>A second, independent service stack on the same database, like a second web request.</summary>
        public CampaignService NewCampaignService(IEmailService email)
        {
            var ws = NewWorkspaceContext();
            return new CampaignService(ws, App, new NotificationService(ws, App), email, _config, NullLogger<CampaignService>.Instance);
        }

        private static User U(int id, string name, string type)
            => new() { Id = id, Name = name, Email = name.ToLower() + "@example.com", UserType = type, PasswordHash = "x", IsActive = true };

        public async Task<Project> AddProjectAsync(string name, params int[] memberIds)
        {
            var p = new Project { Name = name, ProjectNumber = "PRJ-" + name, CreatedByUserId = 1, Status = "active" };
            foreach (var id in memberIds) p.AssignedEmployees.Add(await App.Users.FindAsync(id) ?? throw new InvalidOperationException());
            App.Projects.Add(p);
            await App.SaveChangesAsync();
            return p;
        }

        public Task<List<string>> NotificationMessagesAsync(int userId)
            => Ws.Notifications.Where(n => n.UserId == userId).OrderBy(n => n.Id).Select(n => n.Type).ToListAsync();
    }

    public static class Check
    {
        public static T Ok<T>(WsResult<T> result)
        {
            Assert.True(result.Success, $"expected success but got {result.Status}: {result.Message}");
            return result.Data!;
        }

        public static WsResult<T> Fails<T>(WsResult<T> result, int status, string? messagePart = null)
        {
            Assert.False(result.Success, "expected a failure");
            Assert.Equal(status, result.Status);
            if (messagePart != null) Assert.Contains(messagePart, result.Message, StringComparison.OrdinalIgnoreCase);
            return result;
        }
    }
}
