using Microsoft.EntityFrameworkCore;
using TeamTrack.Data;
using TeamTrack.Workspace.Data;
using TeamTrack.Workspace.Dtos;
using TeamTrack.Workspace.Models;

namespace TeamTrack.Workspace.Services
{
    public interface IWorkspaceProjectService
    {
        /// <summary>A ProductManager sees every project; everyone else (and mineOnly) sees projects they are a member of.</summary>
        Task<WsResult<List<WorkspaceProjectDto>>> ListAsync(CurrentUser user, bool mineOnly = false);
        Task<WsResult<WorkspaceProjectDetailDto>> GetAsync(CurrentUser user, int projectId);
        Task<WsResult<List<DocumentDto>>> ListDocumentsAsync(CurrentUser user, int projectId);
        Task<WsResult<DocumentDto>> GetDocumentAsync(CurrentUser user, int projectId, int documentId);
        Task<WsResult<DocumentDto>> AddDocumentAsync(CurrentUser user, int projectId, DocumentForm form);
    }

    public class WorkspaceProjectService : IWorkspaceProjectService
    {
        public const long MaxFileBytes = DocumentFileService.MaxBytes;
        private static readonly string[] DocTypes = ["doc", "sheet", "slides", "link"];
        private static readonly string[] DoneStatuses = ["completed", "closed", "released"];

        private readonly WorkspaceDbContext _db;
        private readonly AppDbContext _app;
        private readonly IDocumentFileService _files;

        public WorkspaceProjectService(WorkspaceDbContext db, AppDbContext app, IDocumentFileService files)
        {
            _db = db;
            _app = app;
            _files = files;
        }

        public async Task<WsResult<List<WorkspaceProjectDto>>> ListAsync(CurrentUser user, bool mineOnly = false)
        {
            var projects = await _app.Projects
                .Where(p => (user.IsPM && !mineOnly) || p.AssignedEmployees.Any(e => e.Id == user.Id))
                .Include(p => p.AssignedEmployees)
                .OrderBy(p => p.Name).ToListAsync();
            var ids = projects.Select(p => p.Id).ToList();
            var stats = await StatsAsync(ids);
            var docCounts = await _db.ProjectDocuments.Where(d => ids.Contains(d.ProjectId))
                .GroupBy(d => d.ProjectId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.N);

            return WsResult<List<WorkspaceProjectDto>>.Ok(projects.Select(p => Fill(new WorkspaceProjectDto(), p, stats, docCounts)).ToList());
        }

        public async Task<WsResult<WorkspaceProjectDetailDto>> GetAsync(CurrentUser user, int projectId)
        {
            var (project, error) = await LoadForMemberAsync<WorkspaceProjectDetailDto>(user, projectId);
            if (project == null) return error!;

            var stats = await StatsAsync([projectId]);
            var docs = await _db.ProjectDocuments.Where(d => d.ProjectId == projectId)
                .OrderByDescending(d => d.UpdatedAt).ThenByDescending(d => d.Id).ToListAsync();
            var detail = Fill(new WorkspaceProjectDetailDto(), project, stats, new Dictionary<int, int> { [projectId] = docs.Count });
            detail.Description = project.Description;
            detail.LeadName = await _app.Users.Where(u => u.Id == project.CreatedByUserId).Select(u => u.Name).FirstOrDefaultAsync() ?? "";
            detail.Documents = await ToDtosAsync(docs, includeContent: false);
            return WsResult<WorkspaceProjectDetailDto>.Ok(detail);
        }

        public async Task<WsResult<List<DocumentDto>>> ListDocumentsAsync(CurrentUser user, int projectId)
        {
            var (project, error) = await LoadForMemberAsync<List<DocumentDto>>(user, projectId, withMembers: false);
            if (project == null) return error!;
            var docs = await _db.ProjectDocuments.Where(d => d.ProjectId == projectId)
                .OrderByDescending(d => d.UpdatedAt).ThenByDescending(d => d.Id).ToListAsync();
            return WsResult<List<DocumentDto>>.Ok(await ToDtosAsync(docs, includeContent: false));
        }

        public async Task<WsResult<DocumentDto>> GetDocumentAsync(CurrentUser user, int projectId, int documentId)
        {
            var (project, error) = await LoadForMemberAsync<DocumentDto>(user, projectId, withMembers: false);
            if (project == null) return error!;
            var doc = await _db.ProjectDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.ProjectId == projectId);
            if (doc == null) return WsResult<DocumentDto>.Fail(404, "Document not found.");
            return WsResult<DocumentDto>.Ok((await ToDtosAsync([doc], includeContent: true))[0]);
        }

        public async Task<WsResult<DocumentDto>> AddDocumentAsync(CurrentUser user, int projectId, DocumentForm form)
        {
            var (project, denied) = await LoadForMemberAsync<DocumentDto>(user, projectId, withMembers: false);
            if (project == null) return denied!;

            var title = form.Title?.Trim() ?? "";
            var type = form.DocType?.Trim().ToLowerInvariant() ?? "";
            var content = string.IsNullOrWhiteSpace(form.Content) ? null : form.Content;
            var url = string.IsNullOrWhiteSpace(form.Url) ? null : form.Url.Trim();

            if (title.Length == 0) return WsResult<DocumentDto>.Fail(400, "A title is required.");
            if (title.Length > 200) return WsResult<DocumentDto>.Fail(400, "The title is too long (max 200 characters).");
            if (!DocTypes.Contains(type)) return WsResult<DocumentDto>.Fail(400, "Type must be doc, sheet, slides or link.");
            if (content != null && content.Length > 100_000) return WsResult<DocumentDto>.Fail(400, "The content is too long (max 100000 characters).");
            if (form.File != null && form.File.Length > MaxFileBytes) return WsResult<DocumentDto>.Fail(400, "The file is too large (max 20 MB).");
            if (url != null && !IsHttpUrl(url)) return WsResult<DocumentDto>.Fail(400, "The link must be a valid http or https URL.");
            if (type == "link" && url == null) return WsResult<DocumentDto>.Fail(400, "A link needs a URL.");
            if (type != "link" && content == null && url == null && form.File == null)
                return WsResult<DocumentDto>.Fail(400, "Add some content, a link or a file.");

            string? fileUrl = null;
            if (form.File != null)
            {
                try { fileUrl = await _files.SaveAsync(form.File); }
                catch (Exception ex) { return WsResult<DocumentDto>.Fail(400, ex.Message); }
            }

            var now = DateTime.UtcNow;
            var doc = new WsProjectDocument
            {
                ProjectId = projectId, Title = title, DocType = type, Content = content, Url = url,
                FileUrl = fileUrl, UploadedByUserId = user.Id, CreatedAt = now, UpdatedAt = now,
            };
            _db.ProjectDocuments.Add(doc);
            await _db.SaveChangesAsync();
            return WsResult<DocumentDto>.Ok((await ToDtosAsync([doc], includeContent: true))[0], "Document added.");
        }

        // ---------- helpers ----------

        internal static bool IsHttpUrl(string url)
            => url.Length <= 2000 && Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

        /// <summary>404 when the project does not exist; 403 unless the caller is a ProductManager or in project_members.</summary>
        private async Task<(TeamTrack.Models.Project? Project, WsResult<T>? Error)> LoadForMemberAsync<T>(CurrentUser user, int projectId, bool withMembers = true)
        {
            var query = _app.Projects.AsQueryable();
            if (withMembers) query = query.Include(p => p.AssignedEmployees);
            var project = await query.FirstOrDefaultAsync(p => p.Id == projectId);
            if (project == null) return (null, WsResult<T>.Fail(404, "Project not found."));

            if (user.IsPM) return (project, null);

            var isMember = withMembers
                ? project.AssignedEmployees.Any(e => e.Id == user.Id)
                : await _app.Projects.AnyAsync(p => p.Id == projectId && p.AssignedEmployees.Any(e => e.Id == user.Id));
            return isMember ? (project, null) : (null, WsResult<T>.Fail(403, "Only project members and Product Managers can view or add documents."));
        }

        private async Task<Dictionary<int, (int Done, int Total)>> StatsAsync(List<int> projectIds)
        {
            var items = await _app.WorkItems
                .Where(w => w.ProjectId != null && projectIds.Contains(w.ProjectId.Value)
                            && (w.WorkType == null || w.WorkType.ToLower() != "bug"))
                .Select(w => new { ProjectId = w.ProjectId!.Value, w.Status })
                .ToListAsync();
            return items.GroupBy(i => i.ProjectId)
                .ToDictionary(g => g.Key, g => (g.Count(i => DoneStatuses.Contains(i.Status)), g.Count()));
        }

        private static T Fill<T>(T dto, TeamTrack.Models.Project p, Dictionary<int, (int Done, int Total)> stats, Dictionary<int, int> docCounts)
            where T : WorkspaceProjectDto
        {
            var (done, total) = stats.GetValueOrDefault(p.Id);
            dto.Id = p.Id; dto.ProjectNumber = p.ProjectNumber; dto.Name = p.Name; dto.Status = p.Status;
            dto.TasksDone = done; dto.TasksTotal = total;
            dto.ProgressPercent = total == 0 ? 0 : (int)Math.Round(done * 100.0 / total);
            dto.DocumentCount = docCounts.GetValueOrDefault(p.Id);
            dto.Members = p.AssignedEmployees.Where(e => e.IsActive).OrderBy(e => e.Name)
                .Select(e => new MemberDto { Id = e.Id, Name = e.Name, Role = e.UserType, ProfilePicture = e.ProfilePicture }).ToList();
            return dto;
        }

        private async Task<List<DocumentDto>> ToDtosAsync(List<WsProjectDocument> docs, bool includeContent)
        {
            var ids = docs.Select(d => d.UploadedByUserId).Distinct().ToList();
            var names = await _app.Users.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name);
            return docs.Select(d => new DocumentDto
            {
                Id = d.Id, ProjectId = d.ProjectId, Title = d.Title, DocType = d.DocType,
                Content = includeContent ? d.Content : null, Url = d.Url, FileUrl = d.FileUrl,
                UploadedByUserId = d.UploadedByUserId, UploadedByName = names.GetValueOrDefault(d.UploadedByUserId, ""),
                CreatedAt = d.CreatedAt, UpdatedAt = d.UpdatedAt,
            }).ToList();
        }
    }
}
