namespace TeamTrack.Workspace.Services
{
    /// <summary>
    /// Content rules for campaign mails, enforced when a mail is sent.
    /// </summary>
    public static class CampaignRules
    {
        public static readonly string[] Placeholders = ["[Your name]", "[Title]", "[Direct phone]", "[First name]"];

        /// <summary>The placeholders still present in any of the texts (case-insensitive), in the order of <see cref="Placeholders"/>.</summary>
        public static List<string> PlaceholdersIn(params string?[] texts)
            => Placeholders.Where(p => texts.Any(t => t != null && t.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();

        /// <summary>The approved wording is "AI-assisted"; "autonomous" claims are flagged for a human to look at.</summary>
        public static bool MentionsAutonom(params string?[] texts)
            => texts.Any(t => t != null && t.Contains("autonom", StringComparison.OrdinalIgnoreCase));

        /// <summary>The reasons a mail cannot be sent yet (the ERROR rules). Empty when it can.</summary>
        public static List<string> SendBlockers(string? clientEmail, params string?[] texts)
        {
            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(clientEmail)) problems.Add("Client email is missing.");
            else if (!CampaignService.IsValidEmail(clientEmail)) problems.Add("Client email is not a valid email address.");

            var left = PlaceholdersIn(texts);
            if (left.Count > 0) problems.Add($"Replace the placeholders before sending: {string.Join(", ", left)}.");
            return problems;
        }
    }
}
