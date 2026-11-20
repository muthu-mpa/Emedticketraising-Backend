using System;
using System.Collections.Generic;

namespace TeamTrack.Models
{
    public class ClientModule
    {
        public int Id { get; set; }
        public string ClientModuleNumber { get; set; } = string.Empty; // CLM-001
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int ClientId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public Client? Client { get; set; }
        public ICollection<WorkItem> WorkItems { get; set; } = [];
    }
}
