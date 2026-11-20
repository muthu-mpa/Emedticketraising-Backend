using System;
using System.Collections.Generic;

namespace TeamTrack.Models
{
    public class Client
    {
        public int Id { get; set; }
        public string ClientNumber { get; set; } = string.Empty; // CLT-001
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int? ProductId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public Product? Product { get; set; }
        public ICollection<Project> Projects { get; set; } = [];
        public ICollection<ClientModule> ClientModules { get; set; } = [];
    }
}
