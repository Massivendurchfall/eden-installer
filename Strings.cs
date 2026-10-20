using System;
using System.Globalization;
using System.Windows.Markup;

namespace EdenInstaller
{
    /// <summary>
    /// Language of the UI: German if Windows runs in German, English otherwise.
    /// Texts live right in the code as pairs, L.T("Deutsch", "English").
    /// </summary>
    static class L
    {
        public static bool German { get; private set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";

        /// <summary>For numbers like "1,5 GB" or "1.5 GB".</summary>
        public static CultureInfo Culture => CultureInfo.GetCultureInfo(German ? "de-DE" : "en-US");

        public static string T(string german, string english) => German ? german : english;

        /// <summary>For testing: --lang de or --lang en.</summary>
        public static void Force(string language) =>
            German = language.StartsWith("de", StringComparison.OrdinalIgnoreCase);

        /// <summary>Folder for updates and DLCs in a fresh install.</summary>
        public static string DlcFolderName => T("Updates und DLCs", "Updates and DLCs");

        /// <summary>Quotation marks as they are written in the language.</summary>
        public static string Q(string text) => T("„" + text + "“", "“" + text + "”");

        /// <summary>"a, b und c" or "a, b and c".</summary>
        public static string Join(System.Collections.Generic.IList<string> parts)
        {
            if (parts.Count == 1) return parts[0];
            var head = new string[parts.Count - 1];
            for (int i = 0; i < head.Length; i++) head[i] = parts[i];
            return string.Join(", ", head) + T(" und ", " and ") + parts[parts.Count - 1];
        }
    }

    /// <summary>For XAML: Text="{local:T De='Ändern', En='Change'}".</summary>
    public sealed class TExtension : MarkupExtension
    {
        public string De { get; set; }
        public string En { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider) => L.T(De, En);
    }
}
