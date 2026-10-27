using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using Object = UnityEngine.Object;

public sealed class ProjectFontCoverage
{
    public int MainCount, FallbackCount, SourceSupportedCount;
    public readonly List<uint> Missing = new List<uint>();
    public readonly List<uint> UnsupportedBySource = new List<uint>();
    public string Summary => $"主字体覆盖 {MainCount}，回退字体覆盖 {FallbackCount}，仍缺 {Missing.Count}（源字体可补 {SourceSupportedCount}）";
}
[Serializable]
public sealed class ProjectFontReplacementOptions
{
    public TMP_FontAsset TargetTMP;
    public Font TargetLegacy;
    public TMP_FontAsset OnlyTMP;
    public Font OnlyLegacy;
    public bool Prefabs = true, Scenes = true, ScriptableObjects = true, DefaultTMPFont;
    public bool ResetMaterial = true, FillMissing = true;
    public string Key => string.Join("|", new[] { Id(TargetTMP), Id(TargetLegacy), Id(OnlyTMP), Id(OnlyLegacy), Prefabs.ToString(), Scenes.ToString(), ScriptableObjects.ToString(), DefaultTMPFont.ToString(), ResetMaterial.ToString(), FillMissing.ToString() });
    static string Id(Object obj) => obj == null ? "null" : obj.GetInstanceID().ToString();
}
public sealed class ProjectFontReplacementPlan
{
    public sealed class Entry { public string Path, Kind, Fingerprint; public int Count; }
    public readonly List<Entry> Entries = new List<Entry>();
    public readonly List<string> Warnings = new List<string>();
    public string OptionsKey;
    public int Count => Entries.Sum(e => e.Count);
}
public sealed class ProjectFontApplyResult
{
    public int ChangedFiles, ChangedReferences, FailedFiles, SkippedFiles, UnsavedScenes;
    public bool Canceled;
    public string BackupDirectory;
    public readonly List<string> Messages = new List<string>();
    public string Summary => $"已修改 {ChangedFiles} 个文件/场景、{ChangedReferences} 处引用，失败 {FailedFiles}、跳过 {SkippedFiles}；{(Canceled ? "已取消后续处理" : "处理结束")}。" + (UnsavedScenes > 0 ? $"其中 {UnsavedScenes} 个场景只在编辑器中更新，尚未保存。" : "") + "详情见报告。";
}

public static class ProjectFontPipeline
{
    public const string DefaultFontPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/ChillKai SDF 2048 v2.asset";
    public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    public static string OutputDirectory => Path.Combine(ProjectRoot, "output/FontTools");
    public const string LegacyCharacterSetAssetPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/ProjectCharacters.txt";
    static string Absolute(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ProjectRoot, path));
    public static string AssetPath(string path)
    {
        string full = Absolute(path);
        if (!full.StartsWith(ProjectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "";
        return full.Substring(ProjectRoot.Length + 1).Replace('\\', '/');
    }
    public static void VerifyPath(string path, bool writableAsset = false)
    {
        string full = Absolute(path);
        if (!full.StartsWith(ProjectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("路径不在当前项目中: " + path);
        if (writableAsset && (!AssetPath(full).StartsWith("Assets/", StringComparison.Ordinal) || AssetPath(full).EndsWith(".meta", StringComparison.OrdinalIgnoreCase))) throw new IOException("仅可修改 Assets 内明确的资产文件: " + path);
        for (FileSystemInfo item = File.Exists(full) ? (FileSystemInfo)new FileInfo(full) : new DirectoryInfo(full); item != null; item = item is FileInfo f ? f.Directory : ((DirectoryInfo)item).Parent)
            if (item.Attributes != (FileAttributes)(-1) && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("拒绝经过链接路径: " + item.FullName);
        if (writableAsset && Directory.Exists(full)) throw new IOException("写入目标必须是单个资产文件: " + path);
        if (writableAsset && File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReadOnly) != 0) throw new IOException("文件为只读: " + path);
    }
    public static string NewBackupSession()
    {
        string path = Path.Combine(OutputDirectory, "Backups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        VerifyPath(path); Directory.CreateDirectory(path); return path;
    }
    public static void Backup(string assetPath, string session)
    {
        VerifyPath(assetPath, true); VerifyPath(session);
        foreach (string source in new[] { Absolute(assetPath), Absolute(assetPath) + ".meta" })
        {
            if (!File.Exists(source)) continue;
            string destination = Path.Combine(session, AssetPath(source).Replace('/', Path.DirectorySeparatorChar));
            VerifyPath(destination); Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (!File.Exists(destination)) File.Copy(source, destination, false);
        }
    }
    public static string Fingerprint(string path)
    {
        VerifyPath(path);
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(Absolute(path))) return BitConverter.ToString(sha.ComputeHash(stream));
    }
    public static ProjectFontScanResult Scan(ProjectFontScanOptions options)
    {
        options.ExcludedOutputDirectory = OutputDirectory;
        try
        {
            return ProjectFontTextScanner.Scan(options, file =>
            {
                string asset = AssetPath(file);
                if (asset == LegacyCharacterSetAssetPath) return true;
                if (!asset.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) return false;
                Type type = AssetDatabase.GetMainAssetTypeAtPath(asset);
                return type != null && (typeof(TMP_FontAsset).IsAssignableFrom(type) || typeof(Texture).IsAssignableFrom(type) || typeof(Material).IsAssignableFrom(type));
            }, file => !EditorUtility.DisplayCancelableProgressBar("生成项目字符集", AssetPath(file), .35f));
        }
        finally { EditorUtility.ClearProgressBar(); }
    }
    public static string ExportScan(ProjectFontScanResult scan)
    {
        VerifyPath(OutputDirectory); Directory.CreateDirectory(OutputDirectory);
        string path = Path.Combine(OutputDirectory, "ProjectCharacters.txt");
        string backup = NewBackupSession();
        foreach (string name in new[] { "ProjectCharacters.txt", "ScanReport.txt" })
        {
            string previous = Path.Combine(OutputDirectory, name);
            VerifyPath(previous);
            if (File.Exists(previous)) File.Copy(previous, Path.Combine(backup, "output-" + name), false);
        }
        File.WriteAllText(path, scan.Text, new UTF8Encoding(false));
        var report = new StringBuilder($"扫描文件: {scan.FileCount}\nUnicode 字符: {scan.Characters.Count}\n字符集: {path}\n旧输出备份: {backup}\n\n");
        foreach (string warning in scan.Warnings) report.AppendLine(warning);
        report.AppendLine("\n实际扫描文件：");
        foreach (string file in scan.ScannedFiles) report.AppendLine(AssetPath(file));
        File.WriteAllText(Path.Combine(OutputDirectory, "ScanReport.txt"), report.ToString(), new UTF8Encoding(false));
        return path;
    }
    static bool NeedsGlyph(uint code) => code != 0x200D && !(code >= 0xFE00 && code <= 0xFE0F) && !(code >= 0xE0100 && code <= 0xE01EF);
    static bool Contains(TMP_FontAsset font, uint code, bool fallbacks, HashSet<TMP_FontAsset> visited)
    {
        if (font == null || !visited.Add(font)) return false;
        if (font.characterLookupTable.ContainsKey(code)) return true;
        return fallbacks && font.fallbackFontAssetTable != null && font.fallbackFontAssetTable.Any(f => Contains(f, code, true, visited));
    }
    public static Font ResolveSource(TMP_FontAsset font)
    {
        if (font == null) return null;
        if (font.sourceFontFile != null) return font.sourceFontFile;
        var serialized = new SerializedObject(font);
        Font source = serialized.FindProperty("m_SourceFontFile_EditorRef")?.objectReferenceValue as Font;
        if (source != null) return source;
        string guid = serialized.FindProperty("m_SourceFontFileGUID")?.stringValue;
        return string.IsNullOrEmpty(guid) ? null : AssetDatabase.LoadAssetAtPath<Font>(AssetDatabase.GUIDToAssetPath(guid));
    }
    public static ProjectFontCoverage CheckCoverage(ProjectFontScanResult scan, TMP_FontAsset target, Font source, bool globalFallbacks)
    {
        if (scan == null || target == null) throw new InvalidOperationException("请先扫描并选择 TMP 字体。");
        var result = new ProjectFontCoverage();
        bool sourceReady = source != null && FontEngine.LoadFontFace(source, Math.Max(16, (int)target.faceInfo.pointSize)) == FontEngineError.Success;
        foreach (uint code in scan.Characters.Where(NeedsGlyph))
        {
            if (Contains(target, code, false, new HashSet<TMP_FontAsset>())) { result.MainCount++; continue; }
            var visited = new HashSet<TMP_FontAsset>();
            bool found = Contains(target, code, true, visited);
            if (!found && globalFallbacks && TMP_Settings.instance != null && TMP_Settings.fallbackFontAssets != null) found = TMP_Settings.fallbackFontAssets.Any(f => Contains(f, code, true, visited));
            if (found) { result.FallbackCount++; continue; }
            result.Missing.Add(code);
            if (sourceReady && FontEngine.TryGetGlyphWithUnicodeValue(code, GlyphLoadFlags.LOAD_NO_BITMAP, out _)) result.SourceSupportedCount++;
            else result.UnsupportedBySource.Add(code);
        }
        ExportCoverage(scan, result);
        return result;
    }
    static string Cell(string text) => "\"" + (text ?? "").Replace("\"", "\"\"") + "\"";
    public static void ExportCoverage(ProjectFontScanResult scan, ProjectFontCoverage coverage)
    {
        VerifyPath(OutputDirectory); Directory.CreateDirectory(OutputDirectory);
        var report = new StringBuilder("字符,Unicode,源字体是否缺字,出现文件\n");
        var unavailable = new HashSet<uint>(coverage.UnsupportedBySource);
        foreach (uint code in coverage.Missing)
            report.AppendLine(string.Join(",", Cell(char.ConvertFromUtf32((int)code)), Cell("U+" + code.ToString("X4")), Cell(unavailable.Contains(code) ? "是或未指定源字体" : "否，可补入图集"), Cell(scan.Sources.TryGetValue(code, out var paths) ? string.Join("; ", paths) : "")));
        File.WriteAllText(Path.Combine(OutputDirectory, "MissingCharacters.csv"), report.ToString(), new UTF8Encoding(true));
    }
    static void PrepareAtlasForWrite(TMP_FontAsset font)
    {
        Texture2D[] atlases = font.atlasTextures;
        if (atlases == null || atlases.Length == 0 || atlases[0] == null)
            throw new InvalidOperationException("字体缺少主图集，请完整重建并另存为新 TMP 字体。");
        // TMP 3.0.7 SetupNewAtlasTexture uses the lazy m_AtlasTexture cache when
        // naming persistent sub-assets. Bulk TryAddCharacters does not initialize it.
        Texture2D primary = font.atlasTexture;
        int activeIndex = font.atlasTextureCount - 1;
        if (primary == null || activeIndex < 0 || activeIndex >= atlases.Length || atlases[activeIndex] == null)
            throw new InvalidOperationException("字体的当前图集引用无效，请完整重建并另存为新 TMP 字体。");
        if (!atlases[activeIndex].isReadable)
            throw new InvalidOperationException("字体的当前图集不可读，无法增补字符。");
        if (font.material == null)
            throw new InvalidOperationException("字体缺少材质，请完整重建并另存为新 TMP 字体。");
        // A failed TMP page expansion can leave an allocated page without a name.
        // Keep its texture, glyphs and packing data; only supply the missing name.
        for (int i = 0; i <= activeIndex; i++)
            if (atlases[i] != null && string.IsNullOrEmpty(atlases[i].name))
                atlases[i].name = font.name + " Atlas " + i;
    }
    static void PersistFont(TMP_FontAsset font)
    {
        string path = AssetDatabase.GetAssetPath(font); VerifyPath(path, true);
        if (font.material != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(font.material))) AssetDatabase.AddObjectToAsset(font.material, font);
        foreach (Texture2D atlas in font.atlasTextures.Where(t => t != null))
        {
            if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(atlas))) AssetDatabase.AddObjectToAsset(atlas, font);
            EditorUtility.SetDirty(atlas); AssetDatabase.SaveAssetIfDirty(atlas);
        }
        if (font.material != null) { EditorUtility.SetDirty(font.material); AssetDatabase.SaveAssetIfDirty(font.material); }
        EditorUtility.SetDirty(font); AssetDatabase.SaveAssetIfDirty(font);
    }
    public static TMP_FontAsset BuildNewFont(ProjectFontScanResult scan, Font source, string path, int pointSize, int padding, int atlasSize, bool dynamic, bool multiAtlas)
    {
        if (scan == null || source == null) throw new InvalidOperationException("请先扫描并指定源字体。");
        VerifyPath(path, true);
        if (File.Exists(Absolute(path)) || AssetDatabase.LoadMainAssetAtPath(path) != null) throw new IOException("重建只能使用新文件名，原字体不会被覆盖。");
        if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择 .asset 路径。");
        var font = TMP_FontAsset.CreateFontAsset(source, pointSize, padding, GlyphRenderMode.SDFAA, atlasSize, atlasSize, AtlasPopulationMode.Dynamic, multiAtlas);
        if (font == null) throw new InvalidOperationException("无法读取源字体，请检查 Include Font Data。");
        font.name = Path.GetFileNameWithoutExtension(path);
        AssetDatabase.CreateAsset(font, path);
        try
        {
            PrepareAtlasForWrite(font);
            EditorUtility.DisplayProgressBar("构建新 TMP 字体", "生成图集，不清空原字体…", .5f);
            uint[] needed = scan.Characters.Where(NeedsGlyph).ToArray();
            if (needed.Length > 0) font.TryAddCharacters(needed, out uint[] missing, true);
            font.atlasPopulationMode = dynamic ? AtlasPopulationMode.Dynamic : AtlasPopulationMode.Static;
            return font;
        }
        finally
        {
            try { PersistFont(font); }
            finally { EditorUtility.ClearProgressBar(); }
        }
    }
    public static string SupplementFont(ProjectFontScanResult scan, TMP_FontAsset font, Font source, bool multiAtlas)
    {
        if (scan == null || font == null || source == null) throw new InvalidOperationException("请先扫描并指定字体和源字体。");
        Font originalSource = ResolveSource(font); if (originalSource != null && originalSource != source) throw new InvalidOperationException("增补必须使用原始源字体；更换源字体请完整重建。");
        string path = AssetDatabase.GetAssetPath(font); VerifyPath(path, true);
        string backup = NewBackupSession(); Backup(path, backup);
        foreach (var texture in font.atlasTextures.Where(t => t != null))
        {
            string texturePath = AssetDatabase.GetAssetPath(texture);
            if (!string.IsNullOrEmpty(texturePath) && texturePath != path) Backup(texturePath, backup);
        }
        if (font.material != null) { string materialPath = AssetDatabase.GetAssetPath(font.material); if (!string.IsNullOrEmpty(materialPath) && materialPath != path) Backup(materialPath, backup); }
        AtlasPopulationMode originalMode = font.atlasPopulationMode;
        try
        {
            var serialized = new SerializedObject(font);
            serialized.FindProperty("m_SourceFontFile_EditorRef").objectReferenceValue = source;
            serialized.FindProperty("m_SourceFontFileGUID").stringValue = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
            serialized.ApplyModifiedPropertiesWithoutUndo();
            font.atlasPopulationMode = AtlasPopulationMode.Dynamic;
            font.isMultiAtlasTexturesEnabled = multiAtlas;
            PrepareAtlasForWrite(font);
            uint[] needed = scan.Characters.Where(NeedsGlyph).Where(c => !font.characterLookupTable.ContainsKey(c)).ToArray();
            EditorUtility.DisplayProgressBar("增补现有字体", $"增补 {needed.Length} 个字符，保留现有图集和字形", .5f);
            if (needed.Length > 0) font.TryAddCharacters(needed, out uint[] missing, true);
        }
        finally
        {
            font.atlasPopulationMode = originalMode;
            try { PersistFont(font); }
            finally { EditorUtility.ClearProgressBar(); }
        }
        return backup;
    }
    public static string AtlasSummary(TMP_FontAsset font)
    {
        if (font == null) return "未选择字体";
        var atlases = font.atlasTextures.Where(a => a != null).ToArray();
        long bytes = atlases.Sum(a => UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(a));
        return $"{font.atlasPopulationMode} · {font.characterTable.Count} 字符 · {atlases.Length} 张图集 · {font.atlasWidth}×{font.atlasHeight} · 约 {bytes / 1048576f:F1} MB · 多图集 {(font.isMultiAtlasTexturesEnabled ? "开启" : "关闭")}";
    }
    public static string ApplyFallbacks(TMP_FontAsset target, IList<TMP_FontAsset> fallbacks, bool setGlobalDefault)
    {
        if (target == null) throw new InvalidOperationException("请选择目标字体。");
        var unique = fallbacks.Where(f => f != null).Distinct().ToList();
        foreach (var fallback in unique)
        {
            if (fallback == target || Reaches(fallback, target, new HashSet<TMP_FontAsset>()) || HasCycle(fallback, new HashSet<TMP_FontAsset>(), new HashSet<TMP_FontAsset>())) throw new InvalidOperationException("回退链不能包含自身或形成循环。");
        }
        string path = AssetDatabase.GetAssetPath(target); VerifyPath(path, true);
        string backup = NewBackupSession(); Backup(path, backup);
        Undo.RecordObject(target, "配置字体回退"); target.fallbackFontAssetTable = unique;
        EditorUtility.SetDirty(target); AssetDatabase.SaveAssetIfDirty(target);
        if (setGlobalDefault)
        {
            var settings = TMP_Settings.instance;
            if (settings == null) throw new InvalidOperationException("项目缺少 TMP Settings，字体回退已保存，全局默认未修改。");
            string settingsPath = AssetDatabase.GetAssetPath(settings); VerifyPath(settingsPath, true); Backup(settingsPath, backup);
            Undo.RecordObject(settings, "设置 TMP 默认字体"); var serialized = new SerializedObject(settings);
            serialized.FindProperty("m_defaultFontAsset").objectReferenceValue = target; serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(settings); AssetDatabase.SaveAssetIfDirty(settings);
        }
        return backup;
    }
    static bool HasCycle(TMP_FontAsset current, HashSet<TMP_FontAsset> visited, HashSet<TMP_FontAsset> stack)
    {
        if (current == null) return false;
        if (stack.Contains(current)) return true;
        if (!visited.Add(current)) return false;
        stack.Add(current);
        bool cyclic = current.fallbackFontAssetTable != null && current.fallbackFontAssetTable.Any(f => HasCycle(f, visited, stack));
        stack.Remove(current);
        return cyclic;
    }
    static bool Reaches(TMP_FontAsset current, TMP_FontAsset target, HashSet<TMP_FontAsset> visited)
    {
        if (current == target) return true;
        return current != null && visited.Add(current) && current.fallbackFontAssetTable != null && current.fallbackFontAssetTable.Any(f => Reaches(f, target, visited));
    }
    static bool MatchTMP(Object value, ProjectFontReplacementOptions o) => value is TMP_FontAsset && o.TargetTMP != null && (o.OnlyTMP == null || value == o.OnlyTMP);
    static bool MatchLegacy(Object value, ProjectFontReplacementOptions o) => value is Font && o.TargetLegacy != null && (o.OnlyLegacy == null || value == o.OnlyLegacy);
    static int ProcessObject(Object obj, ProjectFontReplacementOptions o, bool apply)
    {
        if (obj == null || obj is TMP_FontAsset || obj.GetType().Name.StartsWith("TMP_SubMesh", StringComparison.Ordinal)) return 0;
        int changes = 0;
        if (obj is TMP_Text text && (MatchTMP(text.font, o) || text.font == null && o.FillMissing && o.OnlyTMP == null && o.TargetTMP != null))
        {
            if (text.font != o.TargetTMP || o.ResetMaterial && text.fontSharedMaterial != o.TargetTMP.material)
            {
                changes++;
                if (apply) { Undo.RecordObject(text, "替换 TMP 字体"); text.font = o.TargetTMP; if (o.ResetMaterial) text.fontSharedMaterial = o.TargetTMP.material; EditorUtility.SetDirty(text); }
            }
        }
        if (o.FillMissing && o.OnlyLegacy == null && o.TargetLegacy != null)
        {
            if (obj is Text legacy && legacy.font == null) { changes++; if (apply) { Undo.RecordObject(legacy, "填补空字体"); legacy.font = o.TargetLegacy; EditorUtility.SetDirty(legacy); } }

        }
        if (obj is TextMesh mesh && (MatchLegacy(mesh.font, o) || mesh.font == null && o.FillMissing && o.OnlyLegacy == null && o.TargetLegacy != null))
        {
            var renderer = mesh.GetComponent<MeshRenderer>();
            if (mesh.font != o.TargetLegacy || o.ResetMaterial && renderer != null && renderer.sharedMaterial != o.TargetLegacy.material)
            {
                changes++;
                if (apply) { Undo.RecordObject(mesh, "替换 TextMesh 字体"); mesh.font = o.TargetLegacy; EditorUtility.SetDirty(mesh); if (renderer != null) { Undo.RecordObject(renderer, "替换 TextMesh 材质"); renderer.sharedMaterial = o.TargetLegacy.material; EditorUtility.SetDirty(renderer); } }
            }
        }
        var serialized = new SerializedObject(obj); var property = serialized.GetIterator(); bool changed = false;
        while (property.Next(true))
        {
            if (property.propertyType != SerializedPropertyType.ObjectReference || property.propertyPath == "m_Script") continue;
            if (obj is TMP_Text && property.propertyPath == "m_fontAsset") continue;
            if (obj is TextMesh && property.propertyPath == "m_Font") continue;
            if (obj is TMP_Settings && (!o.DefaultTMPFont || property.propertyPath != "m_defaultFontAsset")) continue;
            Object value = property.objectReferenceValue;
            Object replacement = MatchTMP(value, o) ? (Object)o.TargetTMP : MatchLegacy(value, o) ? o.TargetLegacy : null;
            // Input fields retain a separate font reference, which can point at a
            // deleted asset even when their child TMP text has been replaced.
            if (replacement == null && obj is TMP_InputField && property.propertyPath == "m_GlobalFontAsset" && value == null && o.FillMissing && o.OnlyTMP == null)
                replacement = o.TargetTMP;
            if (replacement == null || value == replacement) continue;
            changes++;
            if (apply) { if (!changed) Undo.RecordObject(obj, "替换字体引用"); property.objectReferenceValue = replacement; changed = true; }
        }
        if (changed) { serialized.ApplyModifiedProperties(); EditorUtility.SetDirty(obj); }
        return changes;
    }
    static int ProcessRoots(IEnumerable<GameObject> roots, ProjectFontReplacementOptions o, bool apply) => roots.Sum(root => root.GetComponentsInChildren<Component>(true).Where(c => c != null).Sum(c => ProcessObject(c, o, apply)));
    public static ProjectFontReplacementPlan Preview(ProjectFontReplacementOptions options)
    {
        if (options.TargetTMP == null && options.TargetLegacy == null) throw new InvalidOperationException("至少选择一种目标字体。");
        var plan = new ProjectFontReplacementPlan { OptionsKey = options.Key };
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (options.Prefabs) foreach (string id in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" })) paths[AssetDatabase.GUIDToAssetPath(id)] = "Prefab";
        if (options.Scenes) foreach (string id in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" })) paths[AssetDatabase.GUIDToAssetPath(id)] = "Scene";
        if (options.ScriptableObjects || options.DefaultTMPFont) foreach (string id in AssetDatabase.FindAssets("t:ScriptableObject", new[] { "Assets" })) paths[AssetDatabase.GUIDToAssetPath(id)] = "Asset";
        int index = 0;
        try
        {
            foreach (var item in paths.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (EditorUtility.DisplayCancelableProgressBar("预览字体替换", item.Key, (float)index++ / Math.Max(1, paths.Count))) throw new OperationCanceledException();
                try
                {
                    VerifyPath(item.Key, true); int count = 0;
                    if (item.Value == "Prefab")
                    {
                        var root = AssetDatabase.LoadAssetAtPath<GameObject>(item.Key);
                        if (root == null || PrefabUtility.GetPrefabAssetType(root) == PrefabAssetType.Model) continue;
                        count = ProcessRoots(new[] { root }, options, false);
                    }
                    else if (item.Value == "Scene")
                    {
                        Scene open = SceneManager.GetSceneByPath(item.Key);
                        if (open.IsValid() && open.isLoaded)
                        {
                            count = ProcessRoots(open.GetRootGameObjects(), options, false);
                            if (open.isDirty && count > 0) plan.Warnings.Add("未保存场景将仅在编辑器中替换字体（支持撤销），不自动保存其他修改: " + item.Key);
                        }
                        else
                        {
                            // Dependencies identify candidates without opening scenes during preview.
                            count = AssetDatabase.GetDependencies(item.Key, true).Select(AssetDatabase.LoadMainAssetAtPath).Count(a => MatchTMP(a, options) || MatchLegacy(a, options));
                            if (options.FillMissing) count = Math.Max(1, count);
                        }
                    }
                    else
                    {
                        foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(item.Key))
                            if (obj is ScriptableObject && !(obj is TMP_FontAsset) && (!(obj is TMP_Settings) ? options.ScriptableObjects : options.DefaultTMPFont)) count += ProcessObject(obj, options, false);
                    }
                    if (count > 0) plan.Entries.Add(new ProjectFontReplacementPlan.Entry { Path = item.Key, Kind = item.Value, Count = count, Fingerprint = Fingerprint(item.Key) });
                }
                catch (Exception ex) { plan.Warnings.Add(item.Key + ": " + ex.Message); }
            }
        }
        finally { EditorUtility.ClearProgressBar(); }
        return plan;
    }
    public static ProjectFontApplyResult Apply(ProjectFontReplacementPlan plan, ProjectFontReplacementOptions options)
    {
        if (plan == null || plan.OptionsKey != options.Key) throw new InvalidOperationException("选项已改变，请重新预览。");
        foreach (var entry in plan.Entries) if (Fingerprint(entry.Path) != entry.Fingerprint) throw new InvalidOperationException("预览后文件发生变化，请重新预览: " + entry.Path);
        var result = new ProjectFontApplyResult { BackupDirectory = NewBackupSession() };
        Scene active = SceneManager.GetActiveScene(); int index = 0;
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("项目字体替换");
        try
        {
            foreach (var entry in plan.Entries)
            {
                if (EditorUtility.DisplayCancelableProgressBar("应用字体替换", entry.Path, (float)index++ / Math.Max(1, plan.Entries.Count))) { result.Canceled = true; break; }
                try
                {
                    VerifyPath(entry.Path, true); int count = 0;
                    if (entry.Kind == "Prefab")
                    {
                        Backup(entry.Path, result.BackupDirectory); GameObject root = PrefabUtility.LoadPrefabContents(entry.Path);
                        try
                        {
                            count = ProcessRoots(new[] { root }, options, true);
                            if (count > 0) { PrefabUtility.SaveAsPrefabAsset(root, entry.Path, out bool saved); if (!saved) throw new IOException("Prefab 保存失败"); }
                        }
                        finally { PrefabUtility.UnloadPrefabContents(root); }
                    }
                    else if (entry.Kind == "Scene")
                    {
                        Scene scene = SceneManager.GetSceneByPath(entry.Path); bool opened = !scene.IsValid() || !scene.isLoaded;
                        bool preserveUnsavedChanges = !opened && scene.isDirty;
                        Backup(entry.Path, result.BackupDirectory);
                        if (opened) scene = EditorSceneManager.OpenScene(entry.Path, OpenSceneMode.Additive);
                        try
                        {
                            count = ProcessRoots(scene.GetRootGameObjects(), options, true);
                            if (count > 0)
                            {
                                EditorSceneManager.MarkSceneDirty(scene);
                                if (preserveUnsavedChanges)
                                {
                                    result.UnsavedScenes++;
                                    result.Messages.Add("字体已在编辑器中替换，支持撤销；保留其他未保存修改，请手动保存场景: " + entry.Path);
                                }
                                else if (!EditorSceneManager.SaveScene(scene, entry.Path)) throw new IOException("场景保存失败");
                                if (ProcessRoots(scene.GetRootGameObjects(), options, false) != 0) throw new IOException("场景替换后仍存在未替换字体引用");
                            }
                        }
                        finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
                    }
                    else
                    {
                        Backup(entry.Path, result.BackupDirectory);
                        foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(entry.Path))
                            if (obj is ScriptableObject && !(obj is TMP_FontAsset) && (!(obj is TMP_Settings) ? options.ScriptableObjects : options.DefaultTMPFont)) { int changed = ProcessObject(obj, options, true); count += changed; if (changed > 0) AssetDatabase.SaveAssetIfDirty(obj); }
                    }
                    if (count > 0) {
                        if(entry.Kind == "Prefab") { AssetDatabase.ImportAsset(entry.Path, ImportAssetOptions.ForceSynchronousImport); var savedRoot = AssetDatabase.LoadAssetAtPath<GameObject>(entry.Path); if(savedRoot == null || ProcessRoots(new[] { savedRoot }, options, false) != 0) throw new IOException("替换后校验仍存在未替换引用"); }
                        result.ChangedFiles++; result.ChangedReferences += count;
                    }
                    result.Messages.Add(entry.Path + " : " + count + " 处替换");
                }
                catch (Exception ex) { result.FailedFiles++; result.Messages.Add("失败 " + entry.Path + ": " + ex.Message); }
            }
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
            if (active.IsValid() && active.isLoaded) SceneManager.SetActiveScene(active);
            EditorUtility.ClearProgressBar();
            File.WriteAllText(Path.Combine(result.BackupDirectory, "ReplacementReport.txt"), result.Summary + "\n" + string.Join("\n", result.Messages), new UTF8Encoding(false));
        }
        return result;
    }
}
