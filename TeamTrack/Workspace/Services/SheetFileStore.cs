namespace TeamTrack.Workspace.Services
{
    /// <summary>Keeps the original .xlsx of a shared sheet. Separate from the screenshot and project-document storage.</summary>
    public interface ISheetFileStore
    {
        /// <summary>Saves the bytes and returns the stored path (/uploads/sheets/{guid}.xlsx).</summary>
        Task<string> SaveAsync(byte[] bytes);

        /// <summary>Best effort: a missing file is not an error.</summary>
        void Delete(string? path);
    }

    /// <summary>
    /// Writes to {ContentRoot}/uploads/sheets, which is deliberately outside wwwroot: the files are not served as static
    /// content, so only the API (which checks access) can hand them out.
    /// </summary>
    public class SheetFileStore : ISheetFileStore
    {
        public const string UrlPrefix = "/uploads/sheets/";

        private readonly string _folder;
        private readonly ILogger<SheetFileStore> _logger;

        public SheetFileStore(IWebHostEnvironment env, ILogger<SheetFileStore> logger)
        {
            _folder = Path.Combine(env.ContentRootPath, "uploads", "sheets");
            _logger = logger;
        }

        public async Task<string> SaveAsync(byte[] bytes)
        {
            Directory.CreateDirectory(_folder);
            var name = $"{Guid.NewGuid():N}.xlsx"; // the client's file name is never used on disk
            await File.WriteAllBytesAsync(Path.Combine(_folder, name), bytes);
            return UrlPrefix + name;
        }

        public void Delete(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.Combine(_folder, Path.GetFileName(path)); // GetFileName: never leaves the folder
                if (File.Exists(full)) File.Delete(full);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete the stored sheet file {Path}.", path);
            }
        }
    }
}
