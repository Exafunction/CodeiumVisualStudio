using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Utilities;

namespace CodeiumVS.Languages;
public record LangInfo(string Name, string Identifier, Packets.Language Type,
                       bool IsTypeCharTriggerSupported = false,
                       bool IsFormatDocumentSupported = true,
                       bool IsBraceSimulationSupported = false, bool IsIntelliSenseDisabled = true);

internal class KnownLanguages
{
    public static LangInfo Fallback { get; } =
        new LangInfo("Plain Text", "plaintext", Packets.Language.LANGUAGE_UNSPECIFIED,
                     IsTypeCharTriggerSupported: true, IsFormatDocumentSupported: false);

    public static LangInfo[] Default { get; } = [
        Fallback,
        // clang-format off
        new LangInfo("ABAP", "abap", Packets.Language.LANGUAGE_UNSPECIFIED),
        new LangInfo("Windows Bat", "bat", Packets.Language.LANGUAGE_UNSPECIFIED),
        new LangInfo("BibTeX", "bibtex", Packets.Language.LANGUAGE_UNSPECIFIED),
        new LangInfo("Clojure", "clojure", Packets.Language.LANGUAGE_CLOJURE),
        new LangInfo("Coffeescript", "coffeescript", Packets.Language.LANGUAGE_COFFEESCRIPT),
        new LangInfo("C", "c", Packets.Language.LANGUAGE_C),
        new LangInfo("C++", "cpp", Packets.Language.LANGUAGE_CPP, IsTypeCharTriggerSupported: false, IsFormatDocumentSupported: true, IsBraceSimulationSupported: true, IsIntelliSenseDisabled: false),
        new LangInfo("C#", "csharp", Packets.Language.LANGUAGE_CSHARP, IsTypeCharTriggerSupported: false, IsFormatDocumentSupported: true, IsBraceSimulationSupported: true, IsIntelliSenseDisabled: false),
        new LangInfo("CUDA C++", "cuda-cpp", Packets.Language.LANGUAGE_CUDACPP),
        new LangInfo("CSS", "css", Packets.Language.LANGUAGE_CSS),
        new LangInfo("Diff", "diff", Packets.Language.LANGUAGE_UNSPECIFIED),
        new LangInfo("Dockerfile", "dockerfile", Packets.Language.LANGUAGE_DOCKERFILE),
        new LangInfo("F#", "fsharp", Packets.Language.LANGUAGE_FSHARP),
        new LangInfo("Git", "git-commit and git-rebase", Packets.Language.LANGUAGE_UNSPECIFIED),
        new LangInfo("Go", "go", Packets.Language.LANGUAGE_GO),
        new LangInfo("Groovy", "groovy", Packets.Language.LANGUAGE_GROOVY),
        new LangInfo("Handlebars", "handlebars", Packets.Language.LANGUAGE_HANDLEBARS),
        new LangInfo("Haml", "haml", Packets.Language.LANGUAGE_UNSPECIFIED),
        new LangInfo("HTML", "html", Packets.Language.LANGUAGE_HTML),
        new LangInfo("Ini", "ini", Packets.Language.LANGUAGE_INI),
        new LangInfo("Java", "java", Packets.Language.LANGUAGE_JAVA),
        new LangInfo("JavaScript", "javascript", Packets.Language.LANGUAGE_JAVASCRIPT),
        new LangInfo("JavaScript React", "javascriptreact", Packets.Language.LANGUAGE_JAVASCRIPT),
        new LangInfo("JSON", "json", Packets.Language.LANGUAGE_JSON),
        new LangInfo("JSON with Comments", "jsonc", Packets.Language.LANGUAGE_JSON),
        new LangInfo("LaTeX", "latex", Packets.Language.LANGUAGE_LATEX),
        new LangInfo("Less", "less", Packets.Language.LANGUAGE_LESS),
        new LangInfo("Lua", "lua", Packets.Language.LANGUAGE_LUA),
        new LangInfo("Makefile", "makefile", Packets.Language.LANGUAGE_MAKEFILE),
        new LangInfo("Markdown", "markdown", Packets.Language.LANGUAGE_MARKDOWN),
        new LangInfo("Objective-C", "objective-c", Packets.Language.LANGUAGE_OBJECTIVEC),
        new LangInfo("Objective-C++", "objective-cpp", Packets.Language.LANGUAGE_OBJECTIVECPP),
        new LangInfo("Perl", "perl and perl6", Packets.Language.LANGUAGE_PERL),
        new LangInfo("PHP", "php", Packets.Language.LANGUAGE_PHP),
        new LangInfo("PowerShell", "powershell", Packets.Language.LANGUAGE_POWERSHELL),
        new LangInfo("Pug", "jade, pug", Packets.Language.LANGUAGE_UNSPECIFIED),
        new LangInfo("Python", "python", Packets.Language.LANGUAGE_PYTHON),
        new LangInfo("R", "r", Packets.Language.LANGUAGE_R),
        new LangInfo("Razor (cshtml)", "razor", Packets.Language.LANGUAGE_HTML, IsTypeCharTriggerSupported: true),
        new LangInfo("Ruby", "ruby", Packets.Language.LANGUAGE_RUBY),
        new LangInfo("Rust", "rust", Packets.Language.LANGUAGE_RUST),
        new LangInfo("SCSS", "scss", Packets.Language.LANGUAGE_SCSS),
        new LangInfo("ShaderLab", "shaderlab", Packets.Language.LANGUAGE_UNSPECIFIED),
        new LangInfo("Shell Script (Bash)", "shellscript", Packets.Language.LANGUAGE_SHELL),
        new LangInfo("Slim", "slim", Packets.Language.LANGUAGE_UNSPECIFIED),
        new LangInfo("SQL", "sql", Packets.Language.LANGUAGE_SQL),
        new LangInfo("Stylus", "stylus", Packets.Language.LANGUAGE_UNSPECIFIED),
        new LangInfo("Swift", "swift", Packets.Language.LANGUAGE_SWIFT),
        new LangInfo("TypeScript", "typescript", Packets.Language.LANGUAGE_TYPESCRIPT),
        new LangInfo("TypeScript React", "typescriptreact", Packets.Language.LANGUAGE_TYPESCRIPT),
        new LangInfo("TeX", "tex", Packets.Language.LANGUAGE_UNSPECIFIED),
        new LangInfo("Visual Basic", "vb", Packets.Language.LANGUAGE_VISUALBASIC),
        new LangInfo("Vue", "vue", Packets.Language.LANGUAGE_VUE),
        new LangInfo("Vue HTML", "vue-html", Packets.Language.LANGUAGE_VUE),
        new LangInfo("XML", "xml", Packets.Language.LANGUAGE_XML),
        new LangInfo("XSL", "xsl", Packets.Language.LANGUAGE_XSL),
        new LangInfo("YAML", "yaml", Packets.Language.LANGUAGE_YAML),
    // clang-format on
    ];
}
internal class LanguageEqualityComparer : IEqualityComparer<LangInfo>
{
    public bool Equals(LangInfo x, LangInfo y)
    {
        return x.Identifier.Equals(y.Identifier, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(LangInfo obj) { return obj.Identifier.GetHashCode(); }
}

internal class Mapper
{
    private static readonly Dictionary<string, LangInfo> _languagesByIdentifier =
        KnownLanguages.Default.Distinct(new LanguageEqualityComparer())
            .ToDictionary((LangInfo x) => x.Identifier.ToLowerInvariant());

    private static readonly Dictionary<string, LangInfo> _languagesByName =
        KnownLanguages.Default.ToDictionary((LangInfo x) => x.Name.ToLowerInvariant());

    private static readonly ConcurrentDictionary<IContentType, string> _extensionByContentType =
        new();
    private static readonly ConcurrentDictionary<string, LangInfo> _languageByExtension = new();
    private static readonly ConcurrentDictionary<IContentType, LangInfo> _contentTypeMap = new();
    private static readonly ConcurrentDictionary<IContentType, bool>
        _formatDocumentSupportedByContentType = new();

    public static LangInfo GetLanguage(DocumentView docView)
    {
        return GetLanguage(docView.TextBuffer.ContentType,
                           Path.GetExtension(docView.FilePath)?.Trim('.'));
    }

    public static LangInfo GetLanguage(IContentType contentType, string? fileExtension = null)
    {
        string fileExtension2 = fileExtension;
        return _contentTypeMap.GetOrAdd(contentType,
                                        (IContentType ct) => FindLanguage(ct, fileExtension2));
    }

    public static bool IsFormatDocumentSupported(IContentType contentType,
                                                 string? fileExtension = null)
    {
        string fileExtension2 = fileExtension;
        return _formatDocumentSupportedByContentType.GetOrAdd(
            contentType,
            (IContentType ct) => GetLanguage(ct, fileExtension2).IsFormatDocumentSupported);
    }

    private static LangInfo FindLanguage(IContentType type, string? fileExtension)
    {
        string text = type.TypeName.ToLowerInvariant().Replace("projection", "").Trim();
        string text2 = type.DisplayName.ToLowerInvariant().Replace("projection", "").Trim();
        if (_languagesByIdentifier.TryGetValue(text, out LangInfo value) ||
            _languagesByName.TryGetValue(text, out value) ||
            _languagesByIdentifier.TryGetValue(text2, out value) ||
            _languagesByName.TryGetValue(text2, out value))
        {
            return value;
        }
        foreach (IContentType baseType in type.BaseTypes)
        {
            LangInfo language = FindLanguage(baseType, null);
            if (language is not null && language != KnownLanguages.Fallback) { return language; }
        }
        if (fileExtension == null) { _extensionByContentType.TryGetValue(type, out fileExtension); }
        if (fileExtension != null && _languageByExtension.TryGetValue(fileExtension, out value))
        {
            return value;
        }
        if (string.IsNullOrEmpty(fileExtension)) { return KnownLanguages.Fallback; }
        if (_languagesByIdentifier.TryGetValue(fileExtension, out value) ||
            _languagesByName.TryGetValue(fileExtension, out value))
        {
            _extensionByContentType.TryAdd(type, fileExtension);
            _languageByExtension.TryAdd(fileExtension, value);
            return value;
        }

        return KnownLanguages.Fallback;
    }
}
