namespace TeamTrack.Marketing.Services
{
    /// <summary>Keeps lead attachments. Separate from the screenshot, project-document and sheet storage.</summary>
    public interface IMarketingFileStore
    {
        /// <summary>Saves the stream under a generated name and returns that name.</summary>
        Task<string> SaveAsync(Stream content, string extension);

        /// <summary>The stored file, or null when it is missing.</summary>
        Stream? Open(string storedName);

        /// <summary>Best effort: a missing file is not an error.</summary>
        void Delete(string? storedName);
    }

    /// <summary>
    /// Writes to Storage:MarketingAttachmentsPath, or {ContentRoot}/uploads/marketing by default. That is deliberately outside
    /// wwwroot: the files are never served as static content, only by the API after it checks who may see the lead.
    /// </summary>
    public class MarketingFileStore : IMarketingFileStore
    {
        private readonly string _folder;
        private readonly ILogger<MarketingFileStore> _logger;

        public MarketingFileStore(IWebHostEnvironment env, IConfiguration config, ILogger<MarketingFileStore> logger)
        {
            var configured = config["Storage:MarketingAttachmentsPath"];
            _folder = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(env.ContentRootPath, "uploads", "marketing")
                : Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
            _logger = logger;
        }

        public async Task<string> SaveAsync(Stream content, string extension)
        {
            Directory.CreateDirectory(_folder);
            var name = $"{Guid.NewGuid():N}{extension}"; // the client's file name is never used on disk
            await using var target = new FileStream(Path.Combine(_folder, name), FileMode.CreateNew, FileAccess.Write);
            await content.CopyToAsync(target);
            return name;
        }

        public Stream? Open(string storedName)
        {
            var full = Path.Combine(_folder, Path.GetFileName(storedName)); // GetFileName: never leaves the folder
            return File.Exists(full) ? new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        }

        public void Delete(string? storedName)
        {
            if (string.IsNullOrWhiteSpace(storedName)) return;
            try
            {
                var full = Path.Combine(_folder, Path.GetFileName(storedName));
                if (File.Exists(full)) File.Delete(full);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete the stored marketing file {Name}.", storedName);
            }
        }
    }
}
