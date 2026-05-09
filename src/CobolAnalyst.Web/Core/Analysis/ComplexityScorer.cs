using System.Text.RegularExpressions;
using CobolAnalyst.Web.Models;

namespace CobolAnalyst.Web.Core.Analysis;

/// <summary>
/// Scores source chunks 1–3 (Low/Medium/High) based on nesting depth,
/// call count, and conditional branch count. Handles both COBOL and Visual Basic syntax.
/// Runs before any LLM call.
/// </summary>
public sealed partial class ComplexityScorer
{
    // COBOL: IF / EVALUATE open blocks; VB: If / Select Case / Do While|Until / For Each|Next loops
    [GeneratedRegex(
        @"\bIF\b|\bEVALUATE\b|\bSelect\s+Case\b|\bDo\s+(?:While|Until)\b|\bFor\s+Each\b|\bFor\b(?!\s+Each)",
        RegexOptions.IgnoreCase)]
    private static partial Regex OpenBlock();

    // COBOL: END-IF / END-EVALUATE; VB: End If / End Select / Loop / Next
    [GeneratedRegex(
        @"\bEND-IF\b|\bEND-EVALUATE\b|\bEnd\s+If\b|\bEnd\s+Select\b|\bLoop\b|\bNext\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex CloseBlock();

    // COBOL: PERFORM; VB: Call / method invocations are harder to count, so we count GoSub and Call
    [GeneratedRegex(@"\bPERFORM\b|\bGoSub\b|\bCall\b", RegexOptions.IgnoreCase)]
    private static partial Regex CallStatement();

    // COBOL: WHEN / ELSE / ALSO; VB: Case / Else / ElseIf
    [GeneratedRegex(@"\bWHEN\b|\bELSE\b|\bALSO\b|\bElseIf\b|\bCase\b", RegexOptions.IgnoreCase)]
    private static partial Regex Branch();

    /// <summary>Computes the complexity tier for <paramref name="chunk"/>.</summary>
    public ComplexityTier Score(CobolChunk chunk)
    {
        var text = chunk.SourceText;

        int depth = 0;
        int maxDepth = 0;
        foreach (Match _ in OpenBlock().Matches(text))
        {
            depth++;
            if (depth > maxDepth) maxDepth = depth;
        }
        foreach (Match _ in CloseBlock().Matches(text))
        {
            if (depth > 0) depth--;
        }

        int calls    = CallStatement().Matches(text).Count;
        int branches = Branch().Matches(text).Count;

        if (maxDepth <= 1 && branches <= 3)
            return ComplexityTier.Low;
        if (maxDepth <= 2 || branches <= 8)
            return ComplexityTier.Medium;
        return ComplexityTier.High;
    }
}
