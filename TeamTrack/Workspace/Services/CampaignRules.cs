namespace TeamTrack.Workspace.Services
{
    /// <summary>
    /// Content rules for campaign mails, shared by the Excel import (flags) and the submit actions (enforcement),
    /// so a row flagged ERROR in the preview is exactly what the server refuses to submit.
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

        /// <summary>The reasons a mail cannot be sent for approval yet (the ERROR rules). Empty when it can.</summary>
        public static List<string> SubmitBlockers(string? clientEmail, params string?[] texts)
        {
            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(clientEmail)) problems.Add("Client email is missing.");
            else if (!CampaignService.IsValidEmail(clientEmail)) problems.Add("Client email is not a valid email address.");

            var left = PlaceholdersIn(texts);
            if (left.Count > 0) problems.Add($"Replace the placeholders before sending for approval: {string.Join(", ", left)}.");
            return problems;
        }
    }
}
