namespace TeamTrack.Workspace.Services
{
    /// <summary>Storage for project documents. Separate from IFileService, whose screenshot behaviour is unchanged.</summary>
    public interface IDocumentFileService
    {
        /// <summary>Saves the file under /uploads/documents and returns its public URL. Throws <see cref="DocumentFileException"/> for rejected files.</summary>
        Task<string> SaveAsync(IFormFile file);
    }

    public sealed class DocumentFileException : Exception
    {
        public DocumentFileException(string message) : base(message) { }
    }

    public class DocumentFileService : IDocumentFileService
    {
        public const long MaxBytes = 20L * 1024 * 1024;
        public const string UrlPrefix = "/uploads/documents/";

        // extension -> accepted leading bytes (docx/xlsx/pptx are zip containers)
        private static readonly Dictionary<string, byte[][]> Signatures = new()
        {
            [".pdf"] = [[0x25, 0x50, 0x44, 0x46]],
            [".docx"] = [[0x50, 0x4B, 0x03, 0x04]],
            [".xlsx"] = [[0x50, 0x4B, 0x03, 0x04]],
            [".pptx"] = [[0x50, 0x4B, 0x03, 0x04]],
            [".png"] = [[0x89, 0x50, 0x4E, 0x47]],
            [".jpg"] = [[0xFF, 0xD8, 0xFF]],
            [".jpeg"] = [[0xFF, 0xD8, 0xFF]],
        };

        private readonly IWebHostEnvironment _env;

        public DocumentFileService(IWebHostEnvironment env) => _env = env;

        public async Task<string> SaveAsync(IFormFile file)
        {
            if (file.Length == 0) throw new DocumentFileException("The file is empty.");
            if (file.Length > MaxBytes) throw new DocumentFileException("The file is too large (max 20 MB).");

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!Signatures.TryGetValue(extension, out var signatures))
                throw new DocumentFileException("Only pdf, docx, xlsx, pptx, png and jpg files are allowed.");

            await using var input = file.OpenReadStream();
            var header = new byte[8];
            var read = await input.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false);
            if (!signatures.Any(sig => read >= sig.Length && header.AsSpan(0, sig.Length).SequenceEqual(sig)))
                throw new DocumentFileException($"The file content does not look like a {extension.TrimStart('.')} file.");

            var root = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            var folder = Path.Combine(root, "uploads", "documents");
            Directory.CreateDirectory(folder);

            var name = $"{Guid.NewGuid():N}{extension}"; // the client's file name is never used on disk
            await using (var output = new FileStream(Path.Combine(folder, name), FileMode.CreateNew))
            {
                await output.WriteAsync(header.AsMemory(0, read));
                await input.CopyToAsync(output);
            }
            return UrlPrefix + name;
        }
    }
}
