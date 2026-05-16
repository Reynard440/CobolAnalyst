using CobolAnalyst.Web.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace CobolAnalyst.Web.Core.Export;

/// <summary>
/// Generates a professional Word (.docx) report from an <see cref="AnalysisSession"/>.
/// Uses DocumentFormat.OpenXml exclusively — no external processes required.
/// </summary>
public sealed class WordDocumentExporter
{
    // ── Colour palette ────────────────────────────────────────────────────────
    private const string Dark      = "1E2024";
    private const string Accent    = "FF8C00";
    private const string Blue      = "1F77B4";
    private const string Green     = "00A85C";
    private const string Amber     = "FFA000";
    private const string Red       = "D32F2F";
    private const string GreyText  = "555A63";
    private const string White     = "FFFFFF";
    private const string LightGrey = "F5F5F5";

    // ── Decision colour map ───────────────────────────────────────────────────
    private static (string Bg, string Fg) DecisionColour(ReviewDecision d) => d switch
    {
        ReviewDecision.Accepted => ("E8F5E9", Green),
        ReviewDecision.Rejected => ("FFEBEE", Red),
        ReviewDecision.Flagged  => ("FFF8E1", "F57F17"),
        _                       => ("F5F5F5", "757575")
    };

    // ── Risk colour ───────────────────────────────────────────────────────────
    private static string RiskColour(string risk) => risk?.ToLowerInvariant() switch
    {
        "high"   => Red,
        "medium" => "F57F17",
        _        => Green
    };

    // ── A4 page dimensions (twentieths of a point) ────────────────────────────
    private const uint PageWidth   = 11906;
    private const uint PageHeight  = 16838;
    private const uint MarginTopBt = 1440;   // 1 inch
    private const uint MarginLR    = 1134;   // 0.8 inch

    // ── Total usable width for table columns (page width - left - right margins) ──
    // In twentieths of a point → used as relative units for TableWidth / column widths.
    private const int UsableWidth = (int)(PageWidth - MarginLR * 2);   // ≈ 9638

    /// <summary>Generates a .docx report and returns the raw bytes.</summary>
    public byte[] GenerateReport(AnalysisSession session)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            // ── Main document part ──────────────────────────────────────────
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            // ── Page setup ─────────────────────────────────────────────────
            var sectPr = new SectionProperties(
                new PageSize { Width = PageWidth, Height = PageHeight },
                new PageMargin
                {
                    Top    = (int)MarginTopBt,
                    Bottom = (int)MarginTopBt,
                    Left   = MarginLR,
                    Right  = MarginLR
                });

            // ── Cover page ─────────────────────────────────────────────────
            body.AppendChild(MakeHeading("LEGACY CODE ANALYST", 1, Dark, 28));
            body.AppendChild(MakeParagraph("Business Rule Extraction Report", Accent, 15, bold: false));
            body.AppendChild(MakeHorizontalRule(Accent));

            // Stats row (3-column table)
            body.AppendChild(MakeStatsTable(session));

            body.AppendChild(MakeCentredParagraph(
                "INTERNAL USE ONLY — NOT FOR DISTRIBUTION", Red, 9, bold: true, italic: false));
            body.AppendChild(MakeCentredParagraph(
                "All analysis performed locally. No code transmitted externally.", GreyText, 9,
                bold: false, italic: true));

            body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));

            // ── Decision summary ────────────────────────────────────────────
            body.AppendChild(MakeHeading("Decision Summary", 2, Dark, 16));
            body.AppendChild(MakeDecisionSummaryTable(session));
            body.AppendChild(MakeSpacerParagraph());

            // ── Per-file sections ───────────────────────────────────────────
            var fileGroups = BuildFileGroups(session);
            foreach (var (fileName, fileType, rules) in fileGroups)
            {
                var heading = string.IsNullOrWhiteSpace(fileType)
                    ? fileName
                    : $"{fileType}: {fileName}";
                body.AppendChild(MakeHeading(heading, 2, Dark, 14));
                body.AppendChild(MakeParagraph(
                    $"Model used: {session.ModelUsed} | {rules.Count} rules extracted",
                    GreyText, 9, bold: false));
                body.AppendChild(MakeRulesTable(rules));
                body.AppendChild(MakeSpacerParagraph());
            }

            // ── Footer paragraph ────────────────────────────────────────────
            body.AppendChild(MakeCentredParagraph(
                "Generated by CobolAnalyst | All processing performed locally | No external data transmission",
                GreyText, 8, bold: false, italic: true));

            body.AppendChild(sectPr);
            mainPart.Document.Save();
        }

        return ms.ToArray();
    }

    // ── File grouping ─────────────────────────────────────────────────────────

    private static List<(string FileName, string FileType, List<ExtractedRule> Rules)>
        BuildFileGroups(AnalysisSession session)
    {
        var result = new List<(string, string, List<ExtractedRule>)>();

        if (session.Files.Count > 0)
        {
            foreach (var f in session.Files)
            {
                var rules = session.Rules.Where(r => r.SourceFile == f.FileName).ToList();
                var ext   = Path.GetExtension(f.FileName).TrimStart('.').ToUpperInvariant();
                result.Add((f.FileName, ext, rules));
            }
        }
        else
        {
            // Fall back: group by SourceFile when Files list is empty
            foreach (var grp in session.Rules.GroupBy(r => r.SourceFile))
            {
                var ext = Path.GetExtension(grp.Key).TrimStart('.').ToUpperInvariant();
                result.Add((grp.Key, ext, grp.ToList()));
            }
        }

        return result;
    }

    // ── Cover stats table ─────────────────────────────────────────────────────

    private static Table MakeStatsTable(AnalysisSession session)
    {
        var table = new Table();
        table.AppendChild(MakeTableWidth(UsableWidth));

        var row = new TableRow();

        // Compute distinct file count
        var fileCount = session.Files.Count > 0
            ? session.Files.Count
            : session.Rules.Select(r => r.SourceFile).Distinct().Count();

        row.AppendChild(MakeStatCell("RULES IDENTIFIED", session.Rules.Count.ToString()));
        row.AppendChild(MakeStatCell("FILES ANALYSED",   fileCount.ToString()));
        row.AppendChild(MakeStatCell("DATE",             DateTime.Now.ToString("dd MMMM yyyy")));

        table.AppendChild(row);
        return table;
    }

    private static TableCell MakeStatCell(string label, string value)
    {
        var cell = new TableCell();
        cell.AppendChild(MakeTableCellProps(Dark, UsableWidth / 3));

        // Value paragraph (18pt bold white)
        cell.AppendChild(MakeCellParagraph(value, White, 18, bold: true, centred: true));
        // Label paragraph (8pt white)
        cell.AppendChild(MakeCellParagraph(label, White, 8, bold: false, centred: true));

        return cell;
    }

    // ── Decision summary table ─────────────────────────────────────────────────

    private static Table MakeDecisionSummaryTable(AnalysisSession session)
    {
        var table = new Table();
        table.AppendChild(MakeTableWidth(UsableWidth));

        var row = new TableRow();
        row.AppendChild(MakeDecisionCell("ACCEPTED", session.AcceptedCount, ReviewDecision.Accepted));
        row.AppendChild(MakeDecisionCell("REJECTED",  session.RejectedCount,  ReviewDecision.Rejected));
        row.AppendChild(MakeDecisionCell("FLAGGED",   session.FlaggedCount,   ReviewDecision.Flagged));
        row.AppendChild(MakeDecisionCell("PENDING",   session.PendingCount,   ReviewDecision.Pending));
        table.AppendChild(row);

        return table;
    }

    private static TableCell MakeDecisionCell(string label, int count, ReviewDecision d)
    {
        var (bg, fg) = DecisionColour(d);
        var cell = new TableCell();
        cell.AppendChild(MakeTableCellProps(bg, UsableWidth / 4));
        cell.AppendChild(MakeCellParagraph(count.ToString(), fg, 20, bold: true,  centred: true));
        cell.AppendChild(MakeCellParagraph(label,            fg, 9,  bold: true,  centred: true));
        return cell;
    }

    // ── Rules table ──────────────────────────────────────────────────────────

    private static Table MakeRulesTable(List<ExtractedRule> rules)
    {
        // Column widths (proportional, sum = UsableWidth)
        // #(4%) | Description(38%) | Source(18%) | Type(16%) | Risk(8%) | Decision(16%)
        int w0 = (int)(UsableWidth * 0.04);
        int w1 = (int)(UsableWidth * 0.38);
        int w2 = (int)(UsableWidth * 0.18);
        int w3 = (int)(UsableWidth * 0.16);
        int w4 = (int)(UsableWidth * 0.08);
        int w5 = UsableWidth - w0 - w1 - w2 - w3 - w4; // absorb rounding

        var table = new Table();
        table.AppendChild(MakeTableWidth(UsableWidth));

        // Header row
        var header = new TableRow();
        header.AppendChild(MakeTableCell("#",                 Dark, White, 9, true,  w0));
        header.AppendChild(MakeTableCell("Description",       Dark, White, 9, true,  w1));
        header.AppendChild(MakeTableCell("Source Reference",  Dark, White, 9, true,  w2));
        header.AppendChild(MakeTableCell("Type",              Dark, White, 9, true,  w3));
        header.AppendChild(MakeTableCell("Risk",              Dark, White, 9, true,  w4));
        header.AppendChild(MakeTableCell("Decision",          Dark, White, 9, true,  w5));
        table.AppendChild(header);

        // Data rows
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var rowBg = i % 2 == 0 ? White : "F9F9F9";
            var (decBg, decFg) = DecisionColour(rule.Decision);
            var riskFg = RiskColour(rule.Risk);
            var descText = rule.Description.Length > 500
                ? rule.Description[..500] + "…"
                : rule.Description;

            var row = new TableRow();
            row.AppendChild(MakeTableCell((i + 1).ToString(), rowBg, Dark, 8, false, w0));
            row.AppendChild(MakeTableCell(descText,                  rowBg, Dark, 8, false, w1));
            row.AppendChild(MakeTableCell(rule.CobolReference,       rowBg, Dark, 8, false, w2));
            row.AppendChild(MakeTableCell(FormatRuleType(rule.Type), rowBg, Dark, 8, false, w3));
            row.AppendChild(MakeTableCellColoured(
                rule.Risk ?? "low", rowBg, riskFg, 8, w4));
            row.AppendChild(MakeTableCellColoured(
                rule.Decision.ToString(), decBg, decFg, 8, w5));
            table.AppendChild(row);
        }

        return table;
    }

    // ── Paragraph helpers ─────────────────────────────────────────────────────

    private static Paragraph MakeHeading(string text, int level, string colorHex, int fontSize)
    {
        var run = new Run(new Text(text));
        var rpr = new RunProperties(
            new Color { Val = colorHex },
            new FontSize { Val = (fontSize * 2).ToString() },
            new Bold());
        run.PrependChild(rpr);

        var ppr = new ParagraphProperties(
            new SpacingBetweenLines
            {
                Before = level == 1 ? "240" : "200",
                After  = level == 1 ? "120" : "80"
            });

        return new Paragraph(ppr, run);
    }

    private static Paragraph MakeParagraph(string text, string colorHex, int fontSize, bool bold)
    {
        var run = new Run(new Text(text));
        var rpr = new RunProperties(
            new Color { Val = colorHex },
            new FontSize { Val = (fontSize * 2).ToString() });
        if (bold) rpr.AppendChild(new Bold());
        run.PrependChild(rpr);
        return new Paragraph(new ParagraphProperties(
            new SpacingBetweenLines { Before = "60", After = "60" }), run);
    }

    private static Paragraph MakeCentredParagraph(
        string text, string colorHex, int fontSize, bool bold, bool italic)
    {
        var run = new Run(new Text(text));
        var rpr = new RunProperties(
            new Color { Val = colorHex },
            new FontSize { Val = (fontSize * 2).ToString() });
        if (bold)   rpr.AppendChild(new Bold());
        if (italic) rpr.AppendChild(new Italic());
        run.PrependChild(rpr);

        return new Paragraph(
            new ParagraphProperties(
                new Justification { Val = JustificationValues.Center },
                new SpacingBetweenLines { Before = "60", After = "60" }),
            run);
    }

    private static Paragraph MakeHorizontalRule(string colorHex)
    {
        var ppr = new ParagraphProperties(
            new ParagraphBorders(
                new BottomBorder
                {
                    Val   = BorderValues.Single,
                    Color = colorHex,
                    Size  = 8,   // quarter-points → 2pt
                    Space = 1
                }),
            new SpacingBetweenLines { Before = "80", After = "80" });
        return new Paragraph(ppr);
    }

    private static Paragraph MakeSpacerParagraph() =>
        new(new ParagraphProperties(
            new SpacingBetweenLines { Before = "120", After = "120" }));

    // ── Table helpers ─────────────────────────────────────────────────────────

    private static TableProperties MakeTableWidth(int width) =>
        new(
            new TableWidth { Width = width.ToString(), Type = TableWidthUnitValues.Dxa },
            new TableBorders(
                new InsideHorizontalBorder
                    { Val = BorderValues.Single, Color = "E0E0E0", Size = 4 },
                new InsideVerticalBorder
                    { Val = BorderValues.None }),
            new TableLook { Val = "0000" });

    private static TableCellProperties MakeTableCellProps(string bgHex, int width)
    {
        var props = new TableCellProperties(
            new TableCellWidth { Width = width.ToString(), Type = TableWidthUnitValues.Dxa },
            new Shading
            {
                Val   = ShadingPatternValues.Clear,
                Color = "auto",
                Fill  = bgHex
            });
        return props;
    }

    private static TableCell MakeTableCell(
        string text, string bgHex, string fgHex, int fontSize, bool bold, int width)
    {
        var cell = new TableCell();
        cell.AppendChild(MakeTableCellProps(bgHex, width));
        cell.AppendChild(MakeCellParagraph(text, fgHex, fontSize, bold, centred: false));
        return cell;
    }

    private static TableCell MakeTableCellColoured(
        string text, string bgHex, string fgHex, int fontSize, int width)
    {
        var cell = new TableCell();
        cell.AppendChild(MakeTableCellProps(bgHex, width));
        cell.AppendChild(MakeCellParagraph(text, fgHex, fontSize, bold: false, centred: false));
        return cell;
    }

    private static Paragraph MakeCellParagraph(
        string text, string colorHex, int fontSize, bool bold, bool centred)
    {
        var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        var rpr = new RunProperties(
            new Color { Val = colorHex },
            new FontSize { Val = (fontSize * 2).ToString() });
        if (bold) rpr.AppendChild(new Bold());
        run.PrependChild(rpr);

        var ppr = new ParagraphProperties(
            new SpacingBetweenLines { Before = "40", After = "40" });
        if (centred)
            ppr.AppendChild(new Justification { Val = JustificationValues.Center });

        return new Paragraph(ppr, run);
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a <see cref="RuleType"/> enum name to a display string with spaces
    /// (e.g. "DataTransformation" → "Data Transformation").
    /// </summary>
    private static string FormatRuleType(RuleType type)
    {
        var name = type.ToString();
        // Insert a space before each upper-case letter that follows a lower-case letter.
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1]))
                sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }
}
