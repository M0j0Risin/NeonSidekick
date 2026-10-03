using System.Collections.Frozen;

namespace NeonSidekick.UI.Markdown;

/// <summary>
/// The languages a fenced block is highlighted in, and the fence names that reach them
/// (<see cref="Find"/>). A fence with no name or an unknown one stays plain — the lexer never
/// guesses a language from the text (2026-09-22), because a wrong guess paints prose as code.
/// </summary>
public static class CodeLanguages
{
    private static readonly (string Open, string Close)[] SlashStar = [("/*", "*/")];

    public static readonly CodeLanguage CSharp = new(
        "csharp",
        "abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double " +
        "dynamic else enum event explicit extern false file finally fixed float for foreach get global goto if implicit in init int " +
        "interface internal is let lock long nameof namespace new nint not nuint null object operator or and out override params partial " +
        "private protected public readonly record ref required return sbyte scoped sealed set short sizeof stackalloc static string " +
        "struct switch this throw true try typeof uint ulong unchecked unsafe ushort using value var virtual void volatile when where " +
        "while with yield from select orderby group into join")
    {
        LineComments = ["//"],
        BlockComments = SlashStar,
        TripleQuotes = true,
        StringPrefixes = ["$@", "@$", "$$", "$", "@"],
        Preprocessor = true,
        CapitalizedTypes = true,
    };

    public static readonly CodeLanguage JavaScript = new(
        "javascript",
        "as async await break case catch class const continue debugger default delete do else export extends false finally for from " +
        "function get if import in instanceof let new null of return set static super switch this throw true try typeof undefined var " +
        "void while with yield",
        "Array Boolean Date Error JSON Map Math Number Object Promise RegExp Set String Symbol console document window")
    {
        LineComments = ["//"],
        BlockComments = SlashStar,
        Quotes = "\"'`",
        MultilineQuotes = "`",
        AttributePrefix = '@',
        CapitalizedTypes = true,
    };

    public static readonly CodeLanguage TypeScript = new(
        "typescript",
        "abstract any as asserts async await bigint boolean break case catch class const continue debugger declare default delete do " +
        "else enum export extends false finally for from function get if implements import in infer instanceof interface is keyof let " +
        "namespace never new null number object of private protected public readonly return satisfies set static string super switch " +
        "symbol this throw true try type typeof undefined unique unknown var void while with yield",
        "Array Boolean Date Error JSON Map Math Number Object Promise Partial Readonly Record RegExp Set String Symbol console document window")
    {
        LineComments = ["//"],
        BlockComments = SlashStar,
        Quotes = "\"'`",
        MultilineQuotes = "`",
        AttributePrefix = '@',
        CapitalizedTypes = true,
    };

    public static readonly CodeLanguage Python = new(
        "python",
        "False None True and as assert async await break case class continue def del elif else except finally for from global if " +
        "import in is lambda match nonlocal not or pass raise return self cls try while with yield",
        "bool bytes dict float frozenset int list object set str tuple type")
    {
        LineComments = ["#"],
        TripleQuotes = true,
        StringPrefixes = ["rb", "br", "fr", "rf", "f", "r", "b", "u"],
        AttributePrefix = '@',
        CapitalizedTypes = true,
    };

    public static readonly CodeLanguage Shell = new(
        "bash",
        "alias case declare do done elif else esac exit export fi for function if in local readonly return select shift source then " +
        "time trap unset until while",
        "cat cd chmod cp curl echo eval exec find grep ls mkdir mv printf pwd read rm sed set sudo test touch")
    {
        LineComments = ["#"],
        CommentNeedsBoundary = true,
        Quotes = "\"'`",
        MultilineQuotes = "\"'`",
        VariablePrefix = '$',
        DashFlags = true,
    };

    public static readonly CodeLanguage PowerShell = new(
        "powershell",
        "begin break catch class continue data do dynamicparam else elseif end enum exit filter finally for foreach function hidden " +
        "if in param process return static switch throw trap try until using while",
        ignoreCase: true)
    {
        LineComments = ["#"],
        BlockComments = [("<#", "#>")],
        MultilineQuotes = "\"'",
        Escape = '`',
        DoubledQuoteEscapes = true,
        VariablePrefix = '$',
        DashFlags = true,
        DashedNames = true,
    };

    public static readonly CodeLanguage Json = new("json", "true false null")
    {
        LineComments = ["//"],
        BlockComments = SlashStar,
        Quotes = "\"",
        StringKeys = true,
    };

    public static readonly CodeLanguage Yaml = new("yaml", "true false null yes no on off", ignoreCase: true)
    {
        LineComments = ["#"],
        CommentNeedsBoundary = true,
        DoubledQuoteEscapes = true,
        StringKeys = true,
        LineKeySeparator = ':',
    };

    public static readonly CodeLanguage Toml = new("toml", "true false")
    {
        LineComments = ["#", ";"],
        TripleQuotes = true,
        LineKeySeparator = '=',
        Sections = true,
    };

    public static readonly CodeLanguage Sql = new(
        "sql",
        "add all alter and as asc begin between by case check column commit constraint create cross default delete desc distinct " +
        "drop else end exists foreign from full group having if in index inner insert into is join key left like limit merge not null " +
        "offset on or order outer primary references replace returning right rollback select set table then top transaction " +
        "truncate union unique update using values view when where with",
        "bigint binary bit blob bool boolean char date datetime datetime2 decimal double float int integer money nchar numeric " +
        "nvarchar real serial smallint text time timestamp tinyint uniqueidentifier uuid varbinary varchar",
        ignoreCase: true)
    {
        LineComments = ["--"],
        BlockComments = SlashStar,
        Quotes = "'\"`",
        MultilineQuotes = "'",
        Escape = null,
        DoubledQuoteEscapes = true,
    };

    public static readonly CodeLanguage C = new(
        "c",
        "alignas alignof auto bool break case catch char class const const_cast constexpr continue co_await co_return co_yield " +
        "decltype default delete do double dynamic_cast else enum explicit extern false final float for friend goto if inline int long " +
        "mutable namespace new noexcept nullptr operator override private protected public register reinterpret_cast restrict return " +
        "short signed sizeof static static_cast struct switch template this throw true try typedef typename union unsigned using " +
        "virtual void volatile while NULL",
        "int8_t int16_t int32_t int64_t uint8_t uint16_t uint32_t uint64_t size_t ptrdiff_t wchar_t std string vector")
    {
        LineComments = ["//"],
        BlockComments = SlashStar,
        Preprocessor = true,
    };

    public static readonly CodeLanguage Java = new(
        "java",
        "abstract assert boolean break byte case catch char class const continue default do double else enum extends false final " +
        "finally float for goto if implements import instanceof int interface long native new null package permits private protected " +
        "public record return sealed short static strictfp super switch synchronized this throw throws transient true try var void " +
        "volatile while yield")
    {
        LineComments = ["//"],
        BlockComments = SlashStar,
        TripleQuotes = true,
        AttributePrefix = '@',
        CapitalizedTypes = true,
    };

    public static readonly CodeLanguage Kotlin = new(
        "kotlin",
        "abstract as break by catch class companion const constructor continue data do else enum false finally for fun get if import " +
        "in init inline interface internal is lateinit null object open operator out override package private protected public " +
        "return sealed set super suspend this throw true try typealias val var when where while")
    {
        LineComments = ["//"],
        BlockComments = SlashStar,
        TripleQuotes = true,
        AttributePrefix = '@',
        CapitalizedTypes = true,
    };

    public static readonly CodeLanguage Go = new(
        "go",
        "break case chan const continue default defer else fallthrough false for func go goto if import interface iota map nil " +
        "package range return select struct switch true type var",
        "any bool byte complex64 complex128 error float32 float64 int int8 int16 int32 int64 rune string uint uint8 uint16 uint32 " +
        "uint64 uintptr")
    {
        LineComments = ["//"],
        BlockComments = SlashStar,
        Quotes = "\"'`",
        MultilineQuotes = "`",
    };

    public static readonly CodeLanguage Rust = new(
        "rust",
        "as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref " +
        "return self Self static struct super trait true type unsafe use where while",
        "bool char f32 f64 i8 i16 i32 i64 i128 isize str u8 u16 u32 u64 u128 usize")
    {
        LineComments = ["//"],
        BlockComments = SlashStar,
        StringPrefixes = ["br", "b", "r"],
        HashAttributes = true,
        CapitalizedTypes = true,
        Macros = true,
        Lifetimes = true,
    };

    public static readonly CodeLanguage Css = new(
        "css",
        "auto inherit initial none unset important")
    {
        Mode = CodeLexMode.Css,
        BlockComments = SlashStar,
        AttributePrefix = '@',
    };

    public static readonly CodeLanguage Markup = new("xml") { Mode = CodeLexMode.Markup };

    public static readonly CodeLanguage Diff = new("diff") { Mode = CodeLexMode.Diff };

    /// <summary>Fence names (case-insensitive) → language; a file's extension too, for the diff under an edit (<see cref="DiffView.LanguageOf"/>, 2026-10-03, which added the module and MSBuild ones).</summary>
    private static readonly FrozenDictionary<string, CodeLanguage> Aliases = new Dictionary<string, CodeLanguage>(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = CSharp, ["cs"] = CSharp, ["c#"] = CSharp,
        ["javascript"] = JavaScript, ["js"] = JavaScript, ["jsx"] = JavaScript, ["mjs"] = JavaScript, ["node"] = JavaScript,
        ["typescript"] = TypeScript, ["ts"] = TypeScript, ["tsx"] = TypeScript,
        ["python"] = Python, ["py"] = Python, ["python3"] = Python,
        ["bash"] = Shell, ["sh"] = Shell, ["shell"] = Shell, ["zsh"] = Shell, ["console"] = Shell,
        ["powershell"] = PowerShell, ["pwsh"] = PowerShell, ["ps1"] = PowerShell, ["ps"] = PowerShell, ["psm1"] = PowerShell, ["psd1"] = PowerShell,
        ["json"] = Json, ["jsonc"] = Json, ["json5"] = Json,
        ["yaml"] = Yaml, ["yml"] = Yaml,
        ["toml"] = Toml, ["ini"] = Toml, ["cfg"] = Toml,
        ["sql"] = Sql, ["sqlite"] = Sql, ["postgres"] = Sql, ["postgresql"] = Sql, ["mysql"] = Sql, ["tsql"] = Sql,
        ["c"] = C, ["h"] = C, ["cpp"] = C, ["c++"] = C, ["cc"] = C, ["hpp"] = C, ["cxx"] = C,
        ["java"] = Java,
        ["kotlin"] = Kotlin, ["kt"] = Kotlin, ["kts"] = Kotlin,
        ["go"] = Go, ["golang"] = Go,
        ["rust"] = Rust, ["rs"] = Rust,
        ["css"] = Css, ["scss"] = Css, ["less"] = Css,
        ["xml"] = Markup, ["html"] = Markup, ["htm"] = Markup, ["xaml"] = Markup, ["svg"] = Markup, ["csproj"] = Markup, ["xhtml"] = Markup,
        ["props"] = Markup, ["targets"] = Markup, ["slnx"] = Markup, ["resx"] = Markup, ["xsd"] = Markup,
        ["diff"] = Diff, ["patch"] = Diff,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The language a fence names, null when it names none this table knows.</summary>
    public static CodeLanguage? Find(string? fenceLanguage) =>
        fenceLanguage is not null && Aliases.TryGetValue(fenceLanguage, out var language) ? language : null;
}
