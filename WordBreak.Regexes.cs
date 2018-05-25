using System.Text.RegularExpressions;

namespace E368A63F.FreeTypeOnDemand
{
    internal static partial class WordBreak
    {
        /// <remarks>
        ///     <see cref="char.IsLetterOrDigit(char)" /> behaves weird on han characters. using regex as a workaround.
        /// </remarks>
        // TODO complete the regex
        public static readonly Regex LetterOrDigit = new Regex("[a-zA-Z0-9]",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}