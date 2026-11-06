using Microsoft.EntityFrameworkCore;
using TeamTrack.Data;
using TeamTrack.Workspace.Data;
using TeamTrack.Workspace.Dtos;
using TeamTrack.Workspace.Models;

namespace TeamTrack.Workspace.Services
{
    public interface INotificationService
    {
        /// <summary>Active users who can act as a ProductManager (UserType ProductManager or Both).</summary>
        Task<List<int>> PmIdsAsync(int? exceptUserId = null);

        /// <summary>Adds notifications to the context. The caller's SaveChanges commits them with the action itself.</summary>
        void Queue(IEnumerable<int> userIds, string type, string message, string? link);

        Task<NotificationListDto> ListAsync(int userId, int take = 30);
        Task<int> MarkAllReadAsync(int userId);
    }

    public class NotificationService : INotificationService
    {
        private readonly WorkspaceDbContext _db;
        private readonly AppDbContext _app;

        public NotificationService(WorkspaceDbContext db, AppDbContext app)
        {
            _db = db;
            _app = app;
        }

        public Task<List<int>> PmIdsAsync(int? exceptUserId = null)
            => _app.Users
                .Where(u => u.IsActive && (u.UserType == CurrentUser.PmRole || u.UserType == "Both") && u.Id != exceptUserId)
                .Select(u => u.Id)
                .ToListAsync();

        public void Queue(IEnumerable<int> userIds, string type, string message, string? link)
        {
            foreach (var userId in userIds.Distinct())
            {
                _db.Notifications.Add(new WsNotification
                {
                    UserId = userId,
                    Type = type,
                    Message = message,
                    Link = link,
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }

        public async Task<NotificationListDto> ListAsync(int userId, int take = 30)
        {
            take = Math.Clamp(take, 1, 100);
            var items = await _db.Notifications
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
                .Take(take)
                .Select(n => new NotificationDto
                {
                    Id = n.Id, Type = n.Type, Message = n.Message, Link = n.Link,
                    IsRead = n.IsRead, CreatedAt = n.CreatedAt,
                })
                .ToListAsync();
            var unread = await _db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);
            return new NotificationListDto { UnreadCount = unread, Items = items };
        }

        public async Task<int> MarkAllReadAsync(int userId)
        {
            var unread = await _db.Notifications.Where(n => n.UserId == userId && !n.IsRead).ToListAsync();
            foreach (var n in unread) n.IsRead = true;
            await _db.SaveChangesAsync();
            return unread.Count;
        }
    }
}
