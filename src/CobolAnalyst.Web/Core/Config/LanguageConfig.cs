namespace CobolAnalyst.Web.Core.Config;

/// <summary>
/// Defines the full set of supported file extensions and their language names.
/// Mirrors Python config.py ALLOWED_EXTENSIONS and LANGUAGE_MAP.
/// </summary>
public static class LanguageConfig
{
    private static readonly Dictionary<string, string> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // COBOL
        [".cbl"]     = "COBOL",
        [".cob"]     = "COBOL",
        [".cpy"]     = "COBOL Copybook",

        // Visual Basic
        [".vb"]      = "Visual Basic",
        [".bas"]     = "Visual Basic",
        [".cls"]     = "Visual Basic Class",
        [".frm"]     = "Visual Basic Form",
        [".vbs"]     = "VBScript",

        // SQL
        [".sql"]     = "SQL",

        // RPG
        [".rpg"]     = "RPG",
        [".rpgle"]   = "RPG ILE",
        [".sqlrpgle"]= "SQL RPG ILE",

        // PL/I
        [".pli"]     = "PL/I",
        [".pl1"]     = "PL/I",
        [".plx"]     = "PL/I",

        // Natural (Software AG)
        [".nsp"]     = "Natural",
        [".nsa"]     = "Natural",
        [".nsg"]     = "Natural",

        // FORTRAN
        [".f"]       = "FORTRAN",
        [".for"]     = "FORTRAN",
        [".f90"]     = "Fortran 90",
        [".f77"]     = "FORTRAN 77",
        [".f95"]     = "Fortran 95",

        // PowerBuilder
        [".srd"]     = "PowerBuilder DataWindow",
        [".srw"]     = "PowerBuilder Window",
        [".pbl"]     = "PowerBuilder Library",

        // Delphi / Pascal
        [".pas"]     = "Pascal/Delphi",
        [".dpr"]     = "Delphi Project",

        // Ada
        [".ada"]     = "Ada",
        [".adb"]     = "Ada Body",
        [".ads"]     = "Ada Spec",

        // REXX
        [".rexx"]    = "REXX",
        [".rex"]     = "REXX",

        // JCL
        [".jcl"]     = "JCL",
    };

    /// <summary>All supported extensions (excluding the core COBOL/VB/SQL handled by CobolChunker directly).</summary>
    public static readonly string[] AllExtensions = ExtensionMap.Keys.ToArray();

    public static string GetLanguageName(string extension) =>
        ExtensionMap.TryGetValue(extension, out var name) ? name : "Unknown";

    public static bool IsSupported(string extension) =>
        ExtensionMap.ContainsKey(extension);
}
