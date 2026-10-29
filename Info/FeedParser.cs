using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MyDashboard.Info;

/// <summary>
/// RSS 2.0 / RSS 1.0 (RDF) / Atom を、名前空間に依存せず要素名だけで読む簡易パーサー。
/// 日本のニュースサイトはこの3形式のどれかで配信している。
/// </summary>
public static class FeedParser
{
    public static List<NewsItem> Parse(string xml, string sourceName)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new System.IO.StringReader(xml), settings);
        var doc = XDocument.Load(reader);

        var entries = doc.Descendants().Where(e => e.Name.LocalName is "item" or "entry");
        var list = new List<NewsItem>();
        foreach (var e in entries)
        {
            string title = Clean(Child(e, "title")?.Value);
            string link = LinkOf(e);
            if (title.Length == 0 || link.Length == 0) continue;

            var date = ParseDate(Child(e, "pubDate")?.Value)
                    ?? ParseDate(Child(e, "date")?.Value)        // dc:date
                    ?? ParseDate(Child(e, "published")?.Value)   // Atom
                    ?? ParseDate(Child(e, "updated")?.Value);    // Atom
            list.Add(new NewsItem(sourceName, title, link, date, SummaryOf(e, title)));
        }
        return list;
    }

    private static XElement? Child(XElement e, string localName) =>
        e.Elements().FirstOrDefault(c => c.Name.LocalName == localName);

    private static string LinkOf(XElement e)
    {
        foreach (var l in e.Elements().Where(c => c.Name.LocalName == "link"))
        {
            // Atom は <link href="..." rel="alternate"/>、RSS は <link>...</link>
            var href = (string?)l.Attribute("href");
            var rel = (string?)l.Attribute("rel");
            if (!string.IsNullOrWhiteSpace(href) && (rel == null || rel == "alternate")) return href.Trim();
            if (!string.IsNullOrWhiteSpace(l.Value)) return l.Value.Trim();
        }
        var guid = Child(e, "guid")?.Value;
        return guid != null && guid.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? guid.Trim() : "";
    }

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);

    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = WebUtility.HtmlDecode(Tags.Replace(s, ""));
        return Spaces.Replace(s, " ").Trim();
    }

    private static readonly Regex ReadMore = new(@"[（(\[]?\s*(続きを読む|続きを見る|もっと見る|Read more)[\s.。…・>＞»）)\]]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const int SummaryMax = 300;

    /// <summary>
    /// 本文の要約。RSS は description、Atom は summary / content、無ければ content:encoded を使う。
    /// HTML が入っている配信元（GIGAZINE など）は、タグと末尾の「続きを読む」を取り除く。
    /// </summary>
    private static string SummaryOf(XElement e, string title)
    {
        foreach (var name in new[] { "description", "summary", "encoded", "content" })
        {
            var raw = Child(e, name)?.Value;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            // エスケープされた HTML（&lt;p&gt;）も読めるよう、デコードしてからタグを消す
            string s = Clean(WebUtility.HtmlDecode(raw));
            s = ReadMore.Replace(s, "").Trim();
            if (s.Length == 0 || s == title) continue;
            if (s.StartsWith(title, StringComparison.Ordinal)) s = s[title.Length..].TrimStart(' ', '　', '：', ':', '-', '―');
            if (s.Length < 8) continue;
            return s.Length > SummaryMax ? s[..SummaryMax] + "…" : s;
        }
        return "";
    }

    private static readonly Regex OffsetNoColon = new(@"([+-]\d{2})(\d{2})$", RegexOptions.Compiled);

    /// <summary>"Mon, 26 Oct 2026 08:28:28 +0900"（RFC 822）と "2026-10-24T09:58:09+09:00"（ISO 8601）の両方を読む。</summary>
    public static DateTimeOffset? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)) return d;

        // RFC 822 の "+0900" や "JST" / "GMT" を .NET が読める形にそろえる
        string t = s.Replace(" JST", " +09:00").Replace(" GMT", " +00:00").Replace(" UT", " +00:00");
        t = OffsetNoColon.Replace(t, "$1:$2");
        // 先頭の曜日（"Mon, "）は省く
        int comma = t.IndexOf(',');
        if (comma is > 0 and < 5) t = t[(comma + 1)..].Trim();
        string[] formats = { "d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm zzz", "dd MMM yyyy HH:mm:ss zzz" };
        if (DateTimeOffset.TryParseExact(t, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out d)) return d;
        if (DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out d)) return d;
        return null;
    }

    /// <summary>
    /// 複数の配信元を、新しい順を保ちつつ交互に並べる（1つのサイトに偏らないように）。
    /// 24時間より古い見出しは、新しいものが足りないときだけ使う。
    /// </summary>
    public static List<NewsItem> Interleave(IEnumerable<List<NewsItem>> perSource, int maxItems)
    {
        var now = DateTimeOffset.Now;
        var queues = perSource
            .Select(l => new Queue<NewsItem>(l
                .GroupBy(i => i.Link).Select(g => g.First())
                .OrderByDescending(i => i.Published ?? DateTimeOffset.MinValue)
                .Take(10)))
            .Where(q => q.Count > 0)
            .ToList();

        var fresh = new List<NewsItem>();
        var stale = new List<NewsItem>();
        while (queues.Any(q => q.Count > 0) && fresh.Count < maxItems)
        {
            // その周回で各配信元の先頭を取り、新しい順に並べて追加する
            var round = queues.Where(q => q.Count > 0).Select(q => q.Dequeue())
                              .OrderByDescending(i => i.Published ?? DateTimeOffset.MinValue);
            foreach (var item in round)
            {
                if (item.Published is { } p && now - p > TimeSpan.FromHours(24)) stale.Add(item);
                else fresh.Add(item);
            }
        }
        return fresh.Concat(stale).Take(maxItems).ToList();
    }
}
