using System.Text;
using System.Text.RegularExpressions;
using CobolAnalyst.Web.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CobolAnalyst.Web.Core.Generation;

/// <summary>
/// Generates compilable C# class stubs from accepted <see cref="ExtractedRule"/> objects.
/// Uses Roslyn to validate syntax before returning results.
/// </summary>
public sealed partial class CSharpScaffoldGenerator
{
    private readonly string _outputBase;
    private readonly ILogger<CSharpScaffoldGenerator> _logger;

    [GeneratedRegex(@"[^A-Za-z0-9 ]")]
    private static partial Regex NonAlphanumericSpace();

    /// <summary>Initialises the generator with the data output path.</summary>
    public CSharpScaffoldGenerator(IConfiguration config, ILogger<CSharpScaffoldGenerator> logger)
    {
        _logger = logger;
        var dataPath = config["Storage:DataPath"] ?? "./data";
        _outputBase = Path.Combine(dataPath, "generated");
        Directory.CreateDirectory(_outputBase);
    }

    /// <summary>
    /// Generates C# stubs for all accepted rules in the session and writes them to disk.
    /// </summary>
    public async Task<GenerationResult> GenerateAsync(AnalysisSession session)
    {
        var accepted = session.Rules.Where(r => r.Decision == ReviewDecision.Accepted).ToList();

        var byType = accepted.GroupBy(r => r.Type);
        var files = new Dictionary<string, string>();
        var errors = new List<string>();

        foreach (var group in byType)
        {
            var ns = $"CobolAnalyst.Generated.{group.Key}";
            var fileName = $"{group.Key}.cs";
            var content = BuildFileContent(ns, group.ToList());

            var syntaxErrors = ValidateSyntax(content);
            if (syntaxErrors.Count > 0)
            {
                _logger.LogWarning("Syntax errors in {File}: {Errors}", fileName, string.Join("; ", syntaxErrors));
                errors.AddRange(syntaxErrors.Select(e => $"{fileName}: {e}"));
            }

            files[fileName] = content;
        }

        var outDir = Path.Combine(_outputBase, session.Id);
        Directory.CreateDirectory(outDir);

        foreach (var (name, content) in files)
            await File.WriteAllTextAsync(Path.Combine(outDir, name), content);

        _logger.LogInformation("Generated {Count} files for session {Id}", files.Count, session.Id);

        return new GenerationResult
        {
            SessionId = session.Id,
            OutputDirectory = outDir,
            Files = files,
            RuleCount = accepted.Count,
            ValidationErrors = errors
        };
    }

    private string BuildFileContent(string namespaceName, List<ExtractedRule> rules)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated scaffold from CobolAnalyst. Replace NotImplementedExceptions with real logic.");
        sb.AppendLine($"namespace {namespaceName};");
        sb.AppendLine();

        foreach (var rule in rules)
        {
            var className = ToPascalCase(rule.Label);
            sb.AppendLine($"/// <summary>");
            sb.AppendLine($"/// {EscapeXmlDoc(rule.Description)}");
            sb.AppendLine($"/// </summary>");
            sb.AppendLine($"/// <remarks>");
            sb.AppendLine($"/// COBOL Reference: {EscapeXmlDoc(rule.CobolReference)}");
            sb.AppendLine($"/// Migration Notes: {EscapeXmlDoc(rule.MigrationNotes)}");
            sb.AppendLine($"/// </remarks>");
            sb.AppendLine($"public sealed class {className}");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>Executes the {EscapeXmlDoc(rule.Label)} rule.</summary>");
            sb.AppendLine("    public void Execute()");
            sb.AppendLine("    {");
            sb.AppendLine($"        // TODO: Implement {EscapeXmlDoc(rule.Label)}");
            sb.AppendLine("        throw new NotImplementedException();");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static List<string> ValidateSyntax(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var diagnostics = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.GetMessage())
            .ToList();
        return diagnostics;
    }

    private static string ToPascalCase(string label)
    {
        var cleaned = NonAlphanumericSpace().Replace(label, " ");
        return string.Concat(
            cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                   .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    private static string EscapeXmlDoc(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
