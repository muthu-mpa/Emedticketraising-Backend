using System;
using System.Collections.Generic;
using System.Linq;

namespace TeamTrack.Services
{
    public static class SequenceHelper
    {
        public static string GenerateNextNumber(IEnumerable<string?> existingNumbers, string prefix)
        {
            var validNumbers = existingNumbers
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!.Trim())
                .ToList();

            var set = new HashSet<string>(validNumbers, StringComparer.OrdinalIgnoreCase);

            var max = 0;
            foreach (var num in validNumbers)
            {
                var part = num.Split('-').LastOrDefault();
                if (part != null && int.TryParse(part, out var seq) && seq > max)
                {
                    max = seq;
                }
            }

            var nextSeq = Math.Max(max + 1, validNumbers.Count + 1);
            while (set.Contains($"{prefix}-{nextSeq:D3}"))
            {
                nextSeq++;
            }

            return $"{prefix}-{nextSeq:D3}";
        }
    }
}
