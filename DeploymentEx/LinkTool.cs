using System;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Woof.DeploymentEx {

    /// <summary>
    /// A tool for finding download links to the latest version of software.
    /// </summary>
    public static class LinkTool {

        /// <summary>
        /// Gets a link from remote page matching the version pattern.
        /// </summary>
        /// <param name="uri">Initial page to search.</param>
        /// <param name="patterns">Patterns to match when following links, use '*' for version dependent string.</param>
        /// <returns>Link matching the pattern or null if nothing matches.</returns>
        public static Uri FetchLastVersionLink(Uri uri, params string[] patterns) {
            foreach (var pattern in patterns) {
                if (uri == null) return null;
                uri = GetLink(uri, pattern);
            }
            return uri;
        }

        /// <summary>
        /// Gets a link from remote page matching the version pattern.
        /// </summary>
        /// <param name="uri">URL link to the page to crawl.</param>
        /// <param name="pattern">Pattern to match, use '*' for version dependent string.</param>
        /// <returns>Link matching the pattern or null if nothing matches.</returns>
        /// <remarks>
        /// Selecting the latest version depends on whether the version strings are sortable in ascending version order.
        /// </remarks>
        private static Uri GetLink(Uri uri, string pattern) {
            String html, link;
            using (var client = new WebClient()) html = client.DownloadString(uri);
            if (pattern.Contains('*')) {
                var regex = new Regex(Regex.Escape(pattern).Replace("\\*", "(.*?)"));
                link = regex.Matches(html).Cast<Match>().OrderBy(i => i.Groups[1].Value).LastOrDefault()?.Value;
            }
            else link = html.IndexOf(pattern, IgnoreCase) > 0 ? pattern : null;
            if (link == null) return null;
            return new Uri(link.Contains("://") ? link : $"{uri.Scheme}://{uri.Host}{link}");
        }

        private const StringComparison IgnoreCase = StringComparison.InvariantCultureIgnoreCase;

    }

}