using System.Collections.Generic;

namespace Helmsman.Core
{
    internal static class ButtonCodes
    {
        // Parses a comma-separated list of button numbers, ignoring blanks and invalid entries.
        public static int[] Parse(string list)
        {
            var codes = new List<int>();
            foreach (var part in (list ?? "").Split(','))
                if (int.TryParse(part.Trim(), out var code) && code >= 0) codes.Add(code);
            return codes.ToArray();
        }
    }
}
