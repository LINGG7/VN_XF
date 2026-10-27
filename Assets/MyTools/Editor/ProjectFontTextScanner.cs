using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

// Pure text scanning: no Unity asset mutation and no traversal through links.
public sealed class ProjectFontScanOptions
{
    public readonly List<string> Roots = new List<string>();
    public bool IncludeEditorText;
    public bool IncludeCode = true;
    public bool IncludeExcel = true;
    public bool IncludeOtherText;
    public bool IncludeAscii = true;
    public string ExtraCharacters = "，。！？；：“”‘’（）【】《》、·…—～￥";
    public string ExcludedOutputDirectory;
}

public sealed class ProjectFontScanResult
{
    public readonly SortedSet<uint> Characters = new SortedSet<uint>();
    public readonly Dictionary<uint, List<string>> Sources = new Dictionary<uint, List<string>>();
    public readonly List<string> Warnings = new List<string>();
    public readonly List<string> ScannedFiles = new List<string>();
    public int FileCount;
    public string Text => string.Concat(Characters.Select(c => char.ConvertFromUtf32((int)c)));
    public void AddText(string text, string source)
    {
        foreach (uint code in ProjectFontTextScanner.Scalars(text))
        {
            if (code < 32 || code == 127 || (code >= 0xD800 && code <= 0xDFFF)) continue;
            Characters.Add(code);
            if (!Sources.TryGetValue(code, out List<string> paths)) Sources[code] = paths = new List<string>();
            if (paths.Count < 12 && !paths.Contains(source)) paths.Add(source);
        }
    }
}

public static class ProjectFontTextScanner
{
    static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".prefab", ".unity", ".asset", ".cs", ".json", ".txt", ".csv", ".tsv", ".xml", ".uxml", ".uss", ".yaml", ".yml", ".xlsx", ".xls" };
    static readonly HashSet<string> StoryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".csv", ".tsv", ".xlsx", ".xls" };
    static readonly HashSet<string> SkippedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Library", "Temp", "obj", "Logs", "Build", "Builds", "ServerData", "node_modules", "output", ".git", ".vs", "Tests", "Samples", "Samples~", "Documentation~" };
    static readonly Regex Quoted = new Regex("\"(?<value>(?:\\\\.|[^\"\\\\])*)\"", RegexOptions.Singleline);
    static readonly Regex RichTags = new Regex(@"</?(?:b|i|u|s|color|size|font|material|sprite|link|align|alpha|cspace|mspace|voffset|pos|space|indent|line-indent|line-height|margin|width|rotate|nobr|noparse|style|mark|sub|sup|uppercase|lowercase|smallcaps|allcaps|br|page)(?:\s[^>]*|=[^>]*)?/?>", RegexOptions.IgnoreCase);
    static readonly Regex YamlField = new Regex(@"^\s*(?:-\s*)?[\w]+:\s*(.*)$");

    public static IEnumerable<uint> Scalars(string text)
    {
        if (text == null) yield break;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                yield return (uint)char.ConvertToUtf32(c, text[++i]);
            else if (!char.IsSurrogate(c)) yield return c;
        }
    }
    public static string DecodeEscapes(string text)
    {
        var output = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length) { output.Append(text[i]); continue; }
            char kind = text[++i];
            int count = kind == 'u' ? 4 : kind == 'U' ? 8 : 0;
            if (count > 0 && i + count < text.Length && uint.TryParse(text.Substring(i + 1, count), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint code)
                && code <= 0x10FFFF)
            {
                // Keep UTF-16 surrogate escapes together; Scalars joins valid pairs later.
                if (code >= 0xD800 && code <= 0xDFFF) output.Append((char)code);
                else output.Append(char.ConvertFromUtf32((int)code));
                i += count; continue;
            }
            if (kind == 'n') output.Append('\n'); else if (kind == 'r') output.Append('\r');
            else if (kind == 't') output.Append('\t'); else if (kind == '0') output.Append('\0');
            else if (kind == '\\' || kind == '"' || kind == '\'') output.Append(kind);
            else { output.Append('\\'); output.Append(kind); }
        }
        return output.ToString();
    }
    static void Add(ProjectFontScanResult result, string text, string path)
    {
        result.AddText(RichTags.Replace(text ?? "", ""), path);
    }
    static void CollectCode(string text, ref int index, bool interpolationExpression, ProjectFontScanResult result, string path)
    {
        int braces = 0;
        while (index < text.Length)
        {
            char c = text[index];
            if (c == '/' && index + 1 < text.Length && text[index + 1] == '/')
            { while (index < text.Length && text[index] != '\n') index++; continue; }
            if (c == '/' && index + 1 < text.Length && text[index + 1] == '*')
            { int end = text.IndexOf("*/", index + 2, StringComparison.Ordinal); index = end < 0 ? text.Length : end + 2; continue; }
            if (interpolationExpression && c == '}' && braces == 0) { index++; return; }
            if (c == '{') braces++;
            if (c == '}') braces--;
            bool verbatim = false, interpolated = false;
            int quote = index;
            if (c == '$') { interpolated = true; quote++; if (quote < text.Length && text[quote] == '@') { verbatim = true; quote++; } }
            else if (c == '@') { verbatim = true; quote++; if (quote < text.Length && text[quote] == '$') { interpolated = true; quote++; } }
            bool character = c == '\'';
            if (character || quote < text.Length && text[quote] == '"')
            {
                index = quote + 1;
                char delimiter = character ? '\'' : '"';
                var literal = new StringBuilder();
                while (index < text.Length)
                {
                    c = text[index++];
                    if (c == delimiter)
                    {
                        if (verbatim && !character && index < text.Length && text[index] == '"') { literal.Append('"'); index++; continue; }
                        break;
                    }
                    if (!verbatim && c == '\\' && index < text.Length) { literal.Append(c).Append(text[index++]); continue; }
                    if (interpolated && c == '{')
                    {
                        if (index < text.Length && text[index] == '{') { literal.Append('{'); index++; continue; }
                        Add(result, verbatim ? literal.ToString() : DecodeEscapes(literal.ToString()), path);
                        literal.Clear();
                        CollectCode(text, ref index, true, result, path);
                        continue;
                    }
                    if (interpolated && c == '}' && index < text.Length && text[index] == '}') index++;
                    literal.Append(c);
                }
                Add(result, verbatim ? literal.ToString() : DecodeEscapes(literal.ToString()), path);
                continue;
            }
            index++;
        }
    }
    public static void CollectText(string text, string extension, string path, ProjectFontScanResult result)
    {
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            int index = 0;
            CollectCode(text, ref index, false, result, path);
        }
        else if (extension == ".prefab" || extension == ".unity" || extension == ".asset" || extension == ".yaml" || extension == ".yml")
        {
            foreach (Match m in Quoted.Matches(text)) Add(result, DecodeEscapes(m.Groups["value"].Value), path);
            bool block = false;
            foreach (string line in text.Split('\n'))
            {
                Match field = YamlField.Match(line);
                if (field.Success)
                {
                    string value = field.Groups[1].Value.Trim();
                    block = value == "|" || value == ">" || value == "|-" || value == ">-";
                    if (value.Length == 0 || block || value.StartsWith("{") || value.StartsWith("[") || value.StartsWith("\"") || Regex.IsMatch(value, @"^[\d+., eE-]+$")) continue;
                    Add(result, value.Trim('\'').Replace("''", "'"), path);
                }
                else if (block && line.Length > 0 && char.IsWhiteSpace(line[0])) Add(result, line.Trim(), path);
            }
        }
        else if (extension == ".json")
        {
            foreach (Match m in Quoted.Matches(text)) Add(result, DecodeEscapes(m.Groups["value"].Value), path);
        }
        else if (extension == ".xml" || extension == ".uxml")
        {
            using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA) Add(result, reader.Value, path);
                    if (reader.NodeType == XmlNodeType.Element && reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute()) if (reader.Name == "text" || reader.Name == "label" || reader.Name == "tooltip") Add(result, reader.Value, path);
                        reader.MoveToElement();
                    }
                }
        }
        else Add(result, DecodeEscapes(text), path);
    }
    public static ProjectFontScanResult Scan(ProjectFontScanOptions options, Func<string, bool> skipAsset = null, Func<string, bool> continueScan = null)
    {
        var result = new ProjectFontScanResult();
        if (options.IncludeAscii) result.AddText(new string(Enumerable.Range(32, 95).Select(i => (char)i).ToArray()), "[ASCII]");
        result.AddText(options.ExtraCharacters, "[补充字符]");
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string configuredRoot in options.Roots)
        {
            string root = Path.GetFullPath(configuredRoot);
            if (!Directory.Exists(root)) { result.Warnings.Add("扫描目录不存在: " + root); continue; }
            var stack = new Stack<string>(); stack.Push(root);
            while (stack.Count > 0)
            {
                string folder = stack.Pop();
                try
                {
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) { result.Warnings.Add("跳过链接目录: " + folder); continue; }
                    foreach (string child in Directory.EnumerateDirectories(folder).OrderBy(p => p, StringComparer.Ordinal))
                    {
                        string name = Path.GetFileName(child);
                        if (SkippedFolders.Contains(name) || name.StartsWith(".") || (!options.IncludeEditorText && name.Equals("Editor", StringComparison.OrdinalIgnoreCase))) continue;
                        if (!string.IsNullOrEmpty(options.ExcludedOutputDirectory) && Path.GetFullPath(child).StartsWith(Path.GetFullPath(options.ExcludedOutputDirectory), StringComparison.OrdinalIgnoreCase)) continue;
                        stack.Push(child);
                    }
                    foreach (string file in Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.Ordinal))
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (!Extensions.Contains(ext) || !visited.Add(Path.GetFullPath(file))) continue;
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) { result.Warnings.Add("跳过链接文件: " + file); continue; }
                        if (ext == ".cs" && !options.IncludeCode) continue;
                        if (StoryExtensions.Contains(ext) && !options.IncludeExcel) continue;
                        if (!options.IncludeOtherText && ext != ".cs" && ext != ".prefab" && ext != ".unity" && !StoryExtensions.Contains(ext)) continue;
                        if (Path.GetFileName(file).StartsWith("~$")) continue;
                        if (continueScan != null && !continueScan(file)) throw new OperationCanceledException();
                        if (ext == ".xls") { result.Warnings.Add("旧版 .xls 请转换为 .xlsx: " + file); continue; }
                        if (skipAsset != null && skipAsset(file)) continue;
                        try
                        {
                            if (new FileInfo(file).Length > 32L * 1024 * 1024) { result.Warnings.Add("跳过大于 32MB 的文件: " + file); continue; }
                            if (ext == ".xlsx") CollectExcel(file, result);
                            else
                            {
                                using (var input = new StreamReader(file, new UTF8Encoding(false, true), true)) CollectText(input.ReadToEnd(), ext, file, result);
                            }
                            result.FileCount++;
                            result.ScannedFiles.Add(file);
                        }
                        catch (Exception ex) { result.Warnings.Add(file + ": " + ex.Message); }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { result.Warnings.Add(folder + ": " + ex.Message); }
            }
        }
        return result;
    }
    static void CollectExcel(string path, ProjectFontScanResult result)
    {
        using (var archive = ZipFile.OpenRead(path))
            foreach (var entry in archive.Entries)
            {
                if (!(entry.FullName == "xl/sharedStrings.xml" || entry.FullName.StartsWith("xl/worksheets/")) || !entry.FullName.EndsWith(".xml")) continue;
                using (var stream = entry.Open())
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                    while (reader.Read()) if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t") Add(result, reader.ReadElementContentAsString(), path);
            }
    }
}
