using Berichtsportal.Data;
using Berichtsportal.Enums;
using Berichtsportal.Models;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Berichtsportal.Services;

/// <summary>Ergebnis eines PDF-Exports: Byte-Array und vorgeschlagener Dateiname.</summary>
public sealed record PdfExportResult(byte[] Content, string FileName);

/// <summary>
/// Erzeugt PDF-Dateien fuer Wochenberichte – ohne externe Bibliotheken.
///
/// Warum kein iTextSharp / QuestPDF / wkhtmltopdf?
/// - Keine zusaetzliche Abhaengigkeit fuer einen relativ einfachen Anwendungsfall.
/// - Das PDF enthaelt hauptsaechlich Text mit einfacher Formatierung (Fett/Kursiv).
/// - Die Kontrolle ueber das Ausgabeformat bleibt vollstaendig im eigenen Code.
///
/// Stattdessen wird die PDF-Spezifikation direkt umgesetzt:
/// - Objekte (Katalog, Seiten, Fonts, Inhalt) werden manuell aufgebaut.
/// - Helvetica (Type1-Standardfont) wird verwendet, weil er in jedem PDF-Viewer
///   eingebaut ist und keine Einbettung erfordert.
/// - Kodierung: Latin1 / WinAnsiEncoding fuer deutsche Umlaute.
///
/// Registriert als Scoped, weil der Service den DbContext benoetigt.
/// </summary>
public class PdfExportService
{
    // ── Seitengeometrie (A4 in PDF-Punkten, 1 Punkt = 1/72 Zoll) ──
    private const float PageWidth = 595;
    private const float PageHeight = 842;
    private const float Margin = 50;
    private const int FontSize = 11;
    private const int LineHeight = 15;
    private const int MaxCharsPerLine = 92;

    /// <summary>Formfeed-Zeichen als Seitenumbruch-Marker zwischen Report-Abschnitten.</summary>
    private const string PageBreak = "\f";

    private readonly BerichtsportalDbContext context;

    public PdfExportService(BerichtsportalDbContext context)
    {
        this.context = context;
    }

    /// <summary>
    /// Exportiert einen einzelnen Wochenbericht als PDF.
    /// Wird aufgerufen, wenn der Azubi oder Ausbilder einen bestimmten Bericht herunterlaedt.
    /// </summary>
    public async Task<PdfExportResult?> CreateReportPdfAsync(int reportId, User currentUser)
    {
        var report = await context.Reports
            .Include(r => r.Azubi)
            .Include(r => r.Comments.OrderBy(c => c.CreatedAt))
            .FirstOrDefaultAsync(r => r.ReportId == reportId);

        if (report == null || !CanExport(report, currentUser))
            return null;

        var lines = BuildReportLines(report);
        var pdf = BuildPdf(lines);
        var fileName = $"Wochenbericht_KW{report.CalendarWeek}_{report.Year}.pdf";

        return new PdfExportResult(pdf, fileName);
    }

    /// <summary>
    /// Exportiert alle Wochenberichte eines Azubis als ein zusammenhaengendes PDF.
    /// Optional kann ein Zeitraum (von/bis) angegeben werden.
    ///
    /// Ablauf:
    /// 1. Nur Berichte laden, die NICHT im Entwurf-Status sind (Drafts werden gefiltert)
    /// 2. Nach Jahr und Kalenderwoche sortieren
    /// 3. Zusaetzlich nach Datumsbereich filtern (Client-seitig, weil KW→Datum-Umrechnung)
    /// 4. Deckblatt mit Statistik + alle Berichte als einzelne Abschnitte
    /// </summary>
    public async Task<PdfExportResult?> CreateCurrentUserReportsPdfAsync(
        User currentUser,
        DateTime? fromDate,
        DateTime? toDate)
    {
        // Nur Azubis duerfen ihren eigenen Sammelexport erstellen
        if (!currentUser.Role.RoleName.Equals("Auszubildender", StringComparison.OrdinalIgnoreCase))
            return null;

        var reports = await context.Reports
            .Where(r => r.AzubiId == currentUser.UserId)
            .Where(r => r.Status != ReportStatus.Draft)  // Entwuerfe ausschliessen
            .Include(r => r.Azubi)
            .Include(r => r.Comments.OrderBy(c => c.CreatedAt))
            .OrderBy(r => r.Year)
            .ThenBy(r => r.CalendarWeek)
            .ToListAsync();

        // Datumsfilter wird Client-seitig angewendet, weil die Umrechnung
        // von (Jahr + KW) zu einem konkreten Datum nicht in SQL moeglich ist
        reports = reports
            .Where(r => IsInExportRange(r, fromDate, toDate))
            .ToList();

        var lines = BuildExportLines(currentUser, reports, fromDate, toDate);
        var pdf = BuildPdf(lines);
        var fileName = BuildExportFileName(fromDate, toDate);

        return new PdfExportResult(pdf, fileName);
    }

    /// <summary>
    /// Berechtigungspruefung: Darf der aktuelle Benutzer diesen Report exportieren?
    /// - Azubi: nur eigene Berichte
    /// - Ausbilder: nur Berichte der zugeordneten Azubis
    /// </summary>
    private static bool CanExport(Report report, User currentUser)
    {
        if (currentUser.Role.RoleName.Equals("Auszubildender", StringComparison.OrdinalIgnoreCase))
            return report.AzubiId == currentUser.UserId;

        if (currentUser.Role.RoleName.Equals("Ausbilder", StringComparison.OrdinalIgnoreCase))
            return report.Azubi.MentorId == currentUser.UserId;

        return false;
    }

    /// <summary>
    /// Prueft, ob ein Report innerhalb eines optionalen Zeitraums liegt.
    /// Berechnet dafuer den Montag und Sonntag der jeweiligen Kalenderwoche.
    /// </summary>
    private static bool IsInExportRange(Report report, DateTime? fromDate, DateTime? toDate)
    {
        var weekStart = GetFirstDayOfWeek(report.Year, report.CalendarWeek);
        var weekEnd = weekStart.AddDays(6);

        if (fromDate.HasValue && weekEnd.Date < fromDate.Value.Date)
            return false;

        if (toDate.HasValue && weekStart.Date > toDate.Value.Date)
            return false;

        return true;
    }

    /// <summary>
    /// Baut das gesamte PDF-Dokument zeilenweise auf: Deckblatt + alle Report-Abschnitte.
    /// Das Deckblatt enthaelt: Name, Zeitraum, Statistik (freigegeben/eingereicht/abgelehnt).
    /// Jeder Report beginnt auf einer neuen Seite (PageBreak-Marker).
    /// </summary>
    private static List<string> BuildExportLines(
        User currentUser,
        IReadOnlyCollection<Report> reports,
        DateTime? fromDate,
        DateTime? toDate)
    {
        var approved = reports.Count(r => r.Status == ReportStatus.Approved);
        var submitted = reports.Count(r => r.Status == ReportStatus.Submitted);
        var rejected = reports.Count(r => r.Status == ReportStatus.Rejected);

        // Deckblatt: 10 Leerzeilen oben fuer optische Zentrierung
        var lines = new List<string>();

        for (var i = 0; i < 10; i++)
            lines.Add("");

        lines.AddRange(new[]
        {
            "Berichtsheft Export",
            "",
            "",
            "",
            $"Auszubildender: {currentUser.FirstName} {currentUser.LastName}",
            "",
            $"Zeitraum: {FormatDateRange(fromDate, toDate)}",
            "",
            "",
            "",
            $"Anzahl Wochenberichte: {reports.Count}",
            "",
            $"Freigegeben: {approved}",
            $"Eingereicht: {submitted}",
            $"Abgelehnt: {rejected}",
            "",
            "",
            "",
            $"Exportiert am {DateTime.Now:dd.MM.yyyy}"
        });

        if (reports.Count == 0)
        {
            lines.Add("");
            lines.Add("Im ausgewaehlten Zeitraum sind keine Wochenberichte vorhanden.");
            return lines;
        }

        foreach (var report in reports)
        {
            lines.Add(PageBreak);
            lines.AddRange(BuildReportLines(report));
        }

        return lines;
    }

    /// <summary>
    /// Baut die Zeilen fuer einen einzelnen Wochenbericht auf:
    /// Ueberschrift, Arbeitswoche, Status, Trennlinie, dann der eigentliche Inhalt.
    /// Der HTML-Inhalt wird zuvor in Plain-Text mit Format-Markern konvertiert.
    /// </summary>
    private static List<string> BuildReportLines(Report report)
    {
        var weekStart = GetFirstDayOfWeek(report.Year, report.CalendarWeek);
        var weekEnd = weekStart.AddDays(4);

        var lines = new List<string>
        {
            $"Wochenbericht KW {report.CalendarWeek}/{report.Year}",
            "",
            $"Arbeitswoche:   {weekStart:dd.MM.yyyy} - {weekEnd:dd.MM.yyyy}",
            $"Status:         {FormatStatus(report.Status)}",
            "",
            "---",
            ""
        };

        lines.AddRange(WrapText(ConvertHtmlToText(report.Content), MaxCharsPerLine));

        return lines;
    }

    /// <summary>Uebersetzt den Enum-Status in einen deutschen Anzeigetext.</summary>
    private static string FormatStatus(ReportStatus status) => status switch
    {
        ReportStatus.Draft => "Entwurf",
        ReportStatus.Submitted => "Eingereicht",
        ReportStatus.Approved => "Freigegeben",
        ReportStatus.Rejected => "Abgelehnt",
        _ => status.ToString()
    };

    /// <summary>Generiert einen sprechenden Dateinamen basierend auf dem Exportzeitraum.</summary>
    private static string BuildExportFileName(DateTime? fromDate, DateTime? toDate)
    {
        if (!fromDate.HasValue && !toDate.HasValue)
            return "Berichtsheft_Export_Alle.pdf";

        var from = fromDate?.ToString("yyyyMMdd") ?? "Start";
        var to = toDate?.ToString("yyyyMMdd") ?? "Ende";

        return $"Berichtsheft_Export_{from}_{to}.pdf";
    }

    /// <summary>Formatiert den Exportzeitraum fuer die Anzeige auf dem Deckblatt.</summary>
    private static string FormatDateRange(DateTime? fromDate, DateTime? toDate)
    {
        if (!fromDate.HasValue && !toDate.HasValue)
            return "Alle vorhandenen Berichte";

        var from = fromDate?.ToString("dd.MM.yyyy") ?? "Ausbildungsbeginn";
        var to = toDate?.ToString("dd.MM.yyyy") ?? "Ausbildungsende";

        return $"{from} bis {to}";
    }

    // ── Format-Marker (Steuerzeichen) ──
    // HTML-Tags (bold, italic) werden durch nicht-druckbare Steuerzeichen ersetzt.
    // Diese Marker ueberleben den Zeilenumbruch-Algorithmus und werden erst
    // beim PDF-Rendering in Font-Wechsel (Helvetica-Bold, Helvetica-Oblique) uebersetzt.
    //
    // Warum Steuerzeichen statt eigener Datenstruktur?
    // - Einfacher: Der gesamte Text bleibt ein String, keine Span-Liste noetig.
    // - Kompatibel: WrapText() kann Woerter umbrechen, ohne die Formatierung zu verlieren.
    // - Zeichen \x02-\x05 kommen in normalem Text niemals vor.
    private const char BoldOn = '\x02';
    private const char BoldOff = '\x03';
    private const char ItalicOn = '\x04';
    private const char ItalicOff = '\x05';

    /// <summary>
    /// Konvertiert HTML-Inhalt aus dem Rich-Text-Editor in Plain-Text.
    ///
    /// Ablauf:
    /// 1. Bold/Italic-Tags durch Steuerzeichen ersetzen (Format erhalten)
    /// 2. Block-Tags (br, /p, /div, /li) durch Zeilenumbrueche ersetzen
    /// 3. Alle restlichen HTML-Tags entfernen
    /// 4. HTML-Entities dekodieren (z.B. &amp;uml; → ue)
    /// 5. Leerzeichen normalisieren (mehrfache Spaces zusammenfassen)
    /// 6. Leere Zeilen entfernen
    /// </summary>
    private static string ConvertHtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "Kein Inhalt vorhanden.";

        var text = html;

        // Preserve bold and italic formatting as control markers
        text = Regex.Replace(text, @"<(strong|b)\b[^>]*>", BoldOn.ToString(), RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(strong|b)>", BoldOff.ToString(), RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(em|i)\b[^>]*>", ItalicOn.ToString(), RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(em|i)>", ItalicOff.ToString(), RegexOptions.IgnoreCase);

        // Line breaks
        text = Regex.Replace(text, @"<(br|/p|/div|/li)\s*/?>", Environment.NewLine, RegexOptions.IgnoreCase);

        // Strip remaining HTML tags
        text = Regex.Replace(text, "<.*?>", string.Empty);
        text = WebUtility.HtmlDecode(text);

        var markers = new[] { BoldOn, BoldOff, ItalicOn, ItalicOff };
        var normalizedLines = text
            .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
            .Select(line =>
            {
                // Normalize whitespace but keep format markers
                var sb = new StringBuilder();
                var lastWasSpace = true;

                foreach (var ch in line)
                {
                    if (markers.Contains(ch))
                    {
                        sb.Append(ch);
                    }
                    else if (char.IsWhiteSpace(ch))
                    {
                        if (!lastWasSpace)
                        {
                            sb.Append(' ');
                            lastWasSpace = true;
                        }
                    }
                    else
                    {
                        sb.Append(ch);
                        lastWasSpace = false;
                    }
                }

                return sb.ToString().Trim();
            })
            .Where(line => StripMarkers(line).Length > 0);

        return string.Join(Environment.NewLine, normalizedLines);
    }

    /// <summary>Entfernt alle Format-Steuerzeichen aus einem String (fuer Laengenberechnung).</summary>
    private static string StripMarkers(string text)
    {
        return text
            .Replace(BoldOn.ToString(), "")
            .Replace(BoldOff.ToString(), "")
            .Replace(ItalicOn.ToString(), "")
            .Replace(ItalicOff.ToString(), "");
    }

    /// <summary>
    /// Bricht langen Text an Wortgrenzen um (Word-Wrap).
    ///
    /// Besonderheit: Die Laengenberechnung ignoriert Format-Marker,
    /// damit Steuerzeichen nicht als "sichtbare Zeichen" mitgezaehlt werden.
    /// Ein Wort mit Bold-Markern belegt also genauso viel Platz wie ohne.
    /// </summary>
    private static IEnumerable<string> WrapText(string text, int maxChars)
    {
        foreach (var rawLine in text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
        {
            var words = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = new StringBuilder();
            var visibleLength = 0;

            foreach (var word in words)
            {
                var wordVisible = StripMarkers(word).Length;

                if (visibleLength + wordVisible + 1 > maxChars)
                {
                    if (current.Length > 0)
                    {
                        yield return current.ToString();
                        current.Clear();
                        visibleLength = 0;
                    }

                    if (wordVisible > maxChars)
                    {
                        yield return word;
                        continue;
                    }
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                    visibleLength++;
                }

                current.Append(word);
                visibleLength += wordVisible;
            }

            yield return current.Length == 0 ? string.Empty : current.ToString();
        }
    }

    /// <summary>
    /// Baut das vollstaendige PDF-Dokument aus den vorbereiteten Textzeilen.
    ///
    /// PDF-Struktur (vereinfacht):
    /// - Objekt 1: Catalog (Wurzel des Dokuments)
    /// - Objekt 2: Pages (Container fuer alle Seiten)
    /// - Objekte 3-6: Font-Definitionen (Helvetica, Bold, Oblique, BoldOblique)
    /// - Objekte 7+: Abwechselnd Page-Objekt und zugehoeriger Content-Stream
    ///
    /// Seitenumbruch erfolgt entweder durch den PageBreak-Marker ("\f")
    /// oder automatisch, wenn die maximale Zeilenanzahl pro Seite erreicht ist.
    /// </summary>
    private static byte[] BuildPdf(IReadOnlyList<string> lines)
    {
        var linesPerPage = (int)((PageHeight - (2 * Margin)) / LineHeight);
        var pages = new List<List<string>>();
        var currentPage = new List<string>();

        foreach (var line in lines)
        {
            if (line == PageBreak)
            {
                AddCurrentPageIfNeeded();
                continue;
            }

            if (currentPage.Count >= linesPerPage)
                AddCurrentPageIfNeeded();

            currentPage.Add(line);
        }

        AddCurrentPageIfNeeded();

        if (pages.Count == 0)
            pages.Add(new List<string> { "Keine Daten vorhanden." });

        var objects = new List<string>();
        var pageObjectNumbers = new List<int>();

        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add(string.Empty);
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Oblique /Encoding /WinAnsiEncoding >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-BoldOblique /Encoding /WinAnsiEncoding >>");

        foreach (var pageLines in pages)
        {
            var pageObjectNumber = objects.Count + 1;
            var contentObjectNumber = objects.Count + 2;

            pageObjectNumbers.Add(pageObjectNumber);
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth:0} {PageHeight:0}] /Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R /F4 6 0 R >> >> /Contents {contentObjectNumber} 0 R >>");

            var stream = BuildPageContent(pageLines);
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream");
        }

        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', pageObjectNumbers.Select(n => $"{n} 0 R"))}] /Count {pageObjectNumbers.Count} >>";

        return WritePdf(objects);

        void AddCurrentPageIfNeeded()
        {
            if (currentPage.Count == 0)
                return;

            pages.Add(currentPage);
            currentPage = new List<string>();
        }
    }

    /// <summary>
    /// Generiert den PDF-Content-Stream fuer eine einzelne Seite.
    ///
    /// PDF-Textbefehle:
    /// - BT / ET: Begin/End Text Block
    /// - Tm: Text Matrix (Position auf der Seite)
    /// - Tf: Font setzen (z.B. /F1 = Helvetica, /F2 = Helvetica-Bold)
    /// - Tj: Text zeichnen (Hex-kodiert fuer Latin1-Kompatibilitaet)
    ///
    /// Titel-Erkennung: Bestimmte Zeilen (z.B. "Berichtsheft Export") werden
    /// groesser (14pt statt 11pt) und fett dargestellt.
    /// Zentrierte Ausrichtung wird approximiert ueber die geschaetzte Textbreite.
    /// </summary>
    private static string BuildPageContent(IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
        builder.AppendLine("BT");

        var y = PageHeight - Margin;

        foreach (var line in lines)
        {
            var plainLine = StripMarkers(line);
            var isTitle = plainLine.StartsWith("Berichtsheft Export")
                       || plainLine.StartsWith("Wochenbericht KW")
                       || plainLine.StartsWith("Anzahl Wochenberichte");
            var baseSize = isTitle ? 14 : FontSize;

            var textWidth = plainLine.Length * baseSize * 0.52f;
            var x = (PageWidth - textWidth) / 2f;
            if (x < Margin) x = Margin;

            builder.AppendLine($"1 0 0 1 {(int)x} {(int)y} Tm");

            if (isTitle)
            {
                builder.AppendLine($"/F2 {baseSize} Tf");
                builder.Append(ToPdfHexString(plainLine));
                builder.AppendLine(" Tj");
            }
            else if (line.Contains(BoldOn) || line.Contains(ItalicOn))
            {
                RenderFormattedLine(builder, line, baseSize);
            }
            else
            {
                builder.AppendLine($"/F1 {baseSize} Tf");
                builder.Append(ToPdfHexString(line));
                builder.AppendLine(" Tj");
            }

            y -= isTitle ? LineHeight + 4 : LineHeight;
        }

        builder.AppendLine("ET");
        return builder.ToString();
    }

    /// <summary>
    /// Rendert eine Zeile mit gemischter Formatierung (Fett/Kursiv-Wechsel).
    /// Iteriert zeichenweise durch die Zeile und wechselt bei jedem Steuerzeichen
    /// den aktiven Font. Jedes Textsegment wird separat als Tj-Befehl geschrieben.
    /// </summary>
    private static void RenderFormattedLine(StringBuilder builder, string line, int fontSize)
    {
        var bold = false;
        var italic = false;
        var current = new StringBuilder();

        foreach (var ch in line)
        {
            switch (ch)
            {
                case BoldOn:
                    FlushSegment(builder, current, bold, italic, fontSize);
                    bold = true;
                    break;
                case BoldOff:
                    FlushSegment(builder, current, bold, italic, fontSize);
                    bold = false;
                    break;
                case ItalicOn:
                    FlushSegment(builder, current, bold, italic, fontSize);
                    italic = true;
                    break;
                case ItalicOff:
                    FlushSegment(builder, current, bold, italic, fontSize);
                    italic = false;
                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }

        FlushSegment(builder, current, bold, italic, fontSize);
    }

    /// <summary>
    /// Schreibt ein angesammeltes Textsegment in den PDF-Stream.
    /// Waehlt den passenden Font basierend auf dem aktuellen Bold/Italic-Zustand:
    /// F1 = Helvetica, F2 = Bold, F3 = Oblique (Kursiv), F4 = BoldOblique.
    /// </summary>
    private static void FlushSegment(StringBuilder builder, StringBuilder text, bool bold, bool italic, int fontSize)
    {
        if (text.Length == 0)
            return;

        var font = (bold, italic) switch
        {
            (true, true) => "F4",
            (true, false) => "F2",
            (false, true) => "F3",
            _ => "F1"
        };

        builder.AppendLine($"/{font} {fontSize} Tf");
        builder.Append(ToPdfHexString(text.ToString()));
        builder.AppendLine(" Tj");
        text.Clear();
    }

    /// <summary>
    /// Konvertiert Text in einen PDF-Hex-String (&lt;48656C6C6F&gt;).
    /// Verwendet Latin1-Kodierung (WinAnsiEncoding), damit deutsche Umlaute
    /// (ae, oe, ue, ss) korrekt dargestellt werden.
    ///
    /// Warum Hex statt Literal-String?
    /// - Literal-Strings in PDF muessen Sonderzeichen escapen.
    /// - Hex-Strings sind sicherer und einfacher zu erzeugen.
    /// </summary>
    private static string ToPdfHexString(string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        return $"<{Convert.ToHexString(bytes)}>";
    }

    /// <summary>
    /// Schreibt die fertige PDF-Datei gemaess PDF-1.4-Spezifikation:
    /// 1. Header (%PDF-1.4)
    /// 2. Alle Objekte mit fortlaufender Nummerierung
    /// 3. Cross-Reference-Table (Byte-Offsets aller Objekte)
    /// 4. Trailer mit Verweis auf Root-Objekt und xref-Position
    ///
    /// Die xref-Tabelle ist zwingend erforderlich, damit PDF-Viewer
    /// direkt zu einzelnen Objekten springen koennen (Random Access).
    /// </summary>
    private static byte[] WritePdf(IReadOnlyList<string> objects)
    {
        using var stream = new MemoryStream();
        var offsets = new List<long>();

        WriteAscii(stream, "%PDF-1.4\n%Berichtsportal\n");

        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(stream.Position);
            WriteAscii(stream, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefPosition = stream.Position;

        WriteAscii(stream, $"xref\n0 {objects.Count + 1}\n");
        WriteAscii(stream, "0000000000 65535 f \n");

        foreach (var offset in offsets)
            WriteAscii(stream, $"{offset:0000000000} 00000 n \n");

        WriteAscii(stream, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n");
        WriteAscii(stream, $"startxref\n{xrefPosition}\n%%EOF");

        return stream.ToArray();
    }

    /// <summary>Schreibt einen ASCII-String direkt in den Stream (PDF ist ASCII-basiert).</summary>
    private static void WriteAscii(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes);
    }

    /// <summary>
    /// Berechnet den Montag einer bestimmten Kalenderwoche nach ISO 8601.
    /// Verwendet die Deutsche/Europaeische Kalenderregel (FirstFourDayWeek, Montag als Wochenstart).
    /// Wird fuer die Datumsanzeige im Report-Header und fuer den Zeitraumfilter benoetigt.
    /// </summary>
    private static DateTime GetFirstDayOfWeek(int year, int week)
    {
        var jan1 = new DateTime(year, 1, 1);
        var daysOffset = DayOfWeek.Thursday - jan1.DayOfWeek;
        var firstThursday = jan1.AddDays(daysOffset);
        var calendar = System.Globalization.CultureInfo.CurrentCulture.Calendar;
        var firstWeek = calendar.GetWeekOfYear(
            firstThursday,
            System.Globalization.CalendarWeekRule.FirstFourDayWeek,
            DayOfWeek.Monday);

        if (firstWeek <= 1)
            week--;

        return firstThursday.AddDays(week * 7 - 3);
    }
}
