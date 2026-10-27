using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;

public sealed class ProjectFontManagerWindow : EditorWindow
{
    [SerializeField] TMP_FontAsset targetFont;
    [SerializeField] Font sourceFont;
    [SerializeField] ProjectFontReplacementOptions replacement = new ProjectFontReplacementOptions();
    [SerializeField] List<TMP_FontAsset> fallbackFonts = new List<TMP_FontAsset>();
    [SerializeField] bool wholeProject = true, includeCode = true, includeExcel = true, includeEditor, includeAscii = true, useGlobalFallbacks = true;
    [SerializeField] bool includeOtherText;
    [SerializeField] bool dynamicFont, multiAtlas = true, changeGlobalDefault;
    [SerializeField] int pointSize = 48, padding = 5, atlasIndex = 1, tab;
    [SerializeField] string extraCharacters = "，。！？；：“”‘’（）【】《》、·…—～￥";
    Vector2 scroll, planScroll;
    ProjectFontScanResult scan;
    ProjectFontCoverage coverage;
    ProjectFontReplacementPlan plan;
    string status = "先扫描文字生成字符集，再检查字库或预览替换。不会自动修改项目字体。";
    static readonly string[] Tabs = { "字符集", "字库与图集", "全项目替换", "回退字体" };
    static readonly int[] AtlasSizes = { 1024, 2048, 4096 };

    [MenuItem("Tools/VNovelizer/字体管理工具")]
    public static void ShowWindow() => Open();
    public static ProjectFontManagerWindow Open(int page = 0)
    {
        var window = GetWindow<ProjectFontManagerWindow>("项目字体管理");
        window.minSize = new Vector2(720, 600); window.tab = page; window.Show(); return window;
    }
    void OnEnable()
    {
        minSize = new Vector2(720, 600);
        if (targetFont == null) SelectFont(AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(ProjectFontPipeline.DefaultFontPath));
    }
    void SelectFont(TMP_FontAsset font)
    {
        targetFont = font; replacement.TargetTMP = font;
        sourceFont = ProjectFontPipeline.ResolveSource(font);
        fallbackFonts = font != null && font.fallbackFontAssetTable != null ? new List<TMP_FontAsset>(font.fallbackFontAssetTable) : new List<TMP_FontAsset>();
        plan = null; coverage = null;
    }
    void Execute(Action action)
    {
        try { action(); }
        catch (OperationCanceledException) { status = "已取消，没有继续处理。"; }
        catch (Exception ex) { status = ex.Message; Debug.LogException(ex); }
        finally { EditorUtility.ClearProgressBar(); Repaint(); }
    }
    void OnGUI()
    {
        EditorGUILayout.LabelField("项目字体管理", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("字符来源、字库覆盖、图集构建、全项目替换与回退设置", EditorStyles.miniLabel);
        TMP_FontAsset selected = (TMP_FontAsset)EditorGUILayout.ObjectField("目标 TMP 字体", targetFont, typeof(TMP_FontAsset), false);
        if (selected != targetFont) SelectFont(selected);
        sourceFont = (Font)EditorGUILayout.ObjectField("源字体 TTF / OTF", sourceFont, typeof(Font), false);
        EditorGUILayout.HelpBox(ProjectFontPipeline.AtlasSummary(targetFont), MessageType.None);
        tab = GUILayout.Toolbar(tab, Tabs);
        scroll = EditorGUILayout.BeginScrollView(scroll);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
        {
            if (tab == 0) DrawScan(); else if (tab == 1) DrawBuild(); else if (tab == 2) DrawReplacement(); else DrawFallbacks();
        }
        EditorGUILayout.EndScrollView();
        if (tab == 2)
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                DrawReplacementActions();
        if (EditorApplication.isPlayingOrWillChangePlaymode) EditorGUILayout.HelpBox("请退出播放模式后执行字体管理。", MessageType.Info);
        EditorGUILayout.HelpBox(status, MessageType.Info);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("定位生成的 ProjectCharacters.txt")) EditorUtility.RevealInFinder(Path.Combine(ProjectFontPipeline.OutputDirectory, "ProjectCharacters.txt"));
        if (GUILayout.Button("打开字符集、缺字报告与备份目录")) { Directory.CreateDirectory(ProjectFontPipeline.OutputDirectory); EditorUtility.RevealInFinder(ProjectFontPipeline.OutputDirectory); }
        EditorGUILayout.EndHorizontal();
    }
    void DrawScan()
    {
        EditorGUILayout.Space();
        wholeProject = EditorGUILayout.ToggleLeft("包含 Packages 中的代码和资源（默认扫描 Assets；不扫描项目根目录临时副本）", wholeProject);
        includeCode = EditorGUILayout.ToggleLeft("代码字符串（包含运行时拼接文本的固定部分）", includeCode);
        includeExcel = EditorGUILayout.ToggleLeft("剧本文件（XLSX、CSV、TSV）", includeExcel);
        EditorGUILayout.LabelField("场景和 prefab 中的文字始终包含在扫描范围内。", EditorStyles.wordWrappedMiniLabel);
        includeOtherText = EditorGUILayout.ToggleLeft("额外扫描配置和普通文本（asset、JSON、XML、TXT 等，默认关闭）", includeOtherText);
        includeEditor = EditorGUILayout.ToggleLeft("同时收集 Editor 文件夹中的工具文案", includeEditor);
        includeAscii = EditorGUILayout.ToggleLeft("补全可打印 ASCII（数字、英文字母、标点）", includeAscii);
        EditorGUILayout.LabelField("手动补充字符 / 动态内容预计字符");
        extraCharacters = EditorGUILayout.TextArea(extraCharacters, GUILayout.MinHeight(65));
        if (GUILayout.Button("从 TXT 导入补充字符")) Execute(() => { string path = EditorUtility.OpenFilePanel("导入补充字符", "", "txt"); if (!string.IsNullOrEmpty(path)) extraCharacters += File.ReadAllText(path); });
        EditorGUILayout.HelpBox("默认只读取剧本、C# 脚本、场景和 prefab，跳过普通 TXT 字符库及其他配置。需要配置中的本地化/章节名称等文字时，可开启额外扫描；它也可能收集资源路径和内部标识。Editor 工具文案默认跳过。支持 Unicode 转义并记录字符来源。", MessageType.None);
        if (GUILayout.Button("扫描并生成字符集", GUILayout.Height(34))) ScanAll();
        if (scan != null)
        {
            EditorGUILayout.LabelField($"{scan.FileCount} 个文件 / {scan.Characters.Count} 个 Unicode 字符 / {scan.Warnings.Count} 条扫描提示");
            if (scan.Warnings.Count > 0) EditorGUILayout.HelpBox(string.Join("\n", scan.Warnings.Take(6)), MessageType.Warning);
            if (GUILayout.Button("检查目标字体覆盖率")) InspectCoverage();
        }
        DrawCoverage();
    }
    public void ScanAll()
    {
        Execute(() =>
        {
            var options = new ProjectFontScanOptions { IncludeCode = includeCode, IncludeExcel = includeExcel, IncludeOtherText = includeOtherText, IncludeEditorText = includeEditor, IncludeAscii = includeAscii, ExtraCharacters = extraCharacters };
            options.Roots.Add(Application.dataPath);
            string packages = Path.Combine(ProjectFontPipeline.ProjectRoot, "Packages");
            if (wholeProject && Directory.Exists(packages)) options.Roots.Add(packages);
            scan = ProjectFontPipeline.Scan(options); coverage = null;
            string path = ProjectFontPipeline.ExportScan(scan);
            status = $"已扫描 {scan.FileCount} 个文件，生成 {scan.Characters.Count} 个 Unicode 字符。\n{path}\n扫描提示已写入 ScanReport.txt。\n字符集已生成，但尚未写入 TMP 字库。请切到「字库与图集」点击增补。";
        });
    }
    void InspectCoverage()
    {
        Execute(() => { coverage = ProjectFontPipeline.CheckCoverage(scan, targetFont, sourceFont, useGlobalFallbacks); status = coverage.Summary + "。缺字与出现文件已导出 MissingCharacters.csv。"; });
    }
    void DrawCoverage()
    {
        useGlobalFallbacks = EditorGUILayout.ToggleLeft("检查时包含 TMP Settings 全局回退字体", useGlobalFallbacks);
        if (coverage == null) return;
        EditorGUILayout.HelpBox(coverage.Summary, coverage.Missing.Count == 0 ? MessageType.Info : MessageType.Warning);
        if (coverage.Missing.Count > 0)
            EditorGUILayout.SelectableLabel(string.Join("  ", coverage.Missing.Take(80).Select(c => char.ConvertFromUtf32((int)c) + " U+" + c.ToString("X4"))), EditorStyles.wordWrappedLabel, GUILayout.MinHeight(65));
    }
    void DrawBuild()
    {
        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("增补保留现有字形、图集与字体 GUID；完整重建另存为新字体资产，不清空或覆盖原字体。所有现有资产写入前逐文件备份到 output/FontTools/Backups。", MessageType.Info);
        if (GUILayout.Button(scan == null ? "先扫描项目文字" : "重新扫描项目文字")) ScanAll();
        if (GUILayout.Button("检查覆盖率与源字体支持情况")) InspectCoverage();
        DrawCoverage();
        EditorGUILayout.Space();
        multiAtlas = EditorGUILayout.ToggleLeft("允许多图集（避免单张图集容量不足）", multiAtlas);
        using (new EditorGUI.DisabledScope(scan == null || targetFont == null || sourceFont == null))
            if (GUILayout.Button("增补现有 TMP 字体的缺失字形", GUILayout.Height(32))) Execute(() =>
            {
                string backup = ProjectFontPipeline.SupplementFont(scan, targetFont, sourceFont, multiAtlas);
                plan = null; coverage = ProjectFontPipeline.CheckCoverage(scan, targetFont, sourceFont, useGlobalFallbacks);
                status = "增补结束。" + coverage.Summary + "\n备份：" + backup;
            });
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("新建重建字体参数", EditorStyles.boldLabel);
        pointSize = EditorGUILayout.IntSlider("采样字号", pointSize, 16, 128);
        padding = EditorGUILayout.IntSlider("图集留边", padding, 1, 16);
        atlasIndex = EditorGUILayout.Popup("新图集规格", atlasIndex, new[] { "1024 × 1024", "2048 × 2048", "4096 × 4096" });
        dynamicFont = EditorGUILayout.ToggleLeft("新字体保持 Dynamic；关闭则构建完成后设为 Static", dynamicFont);
        EditorGUILayout.LabelField("以上字号与规格只影响新建字体，增补沿用现有图集。", EditorStyles.wordWrappedMiniLabel);
        using (new EditorGUI.DisabledScope(scan == null || sourceFont == null))
            if (GUILayout.Button("完整重建并另存为新 TMP 字体", GUILayout.Height(32))) Execute(() =>
            {
                string path = EditorUtility.SaveFilePanelInProject("另存为新 TMP 字体", sourceFont.name + " Project SDF", "asset", "请选择新文件名，现有字体不会被覆盖。");
                if (string.IsNullOrEmpty(path)) return;
                var created = ProjectFontPipeline.BuildNewFont(scan, sourceFont, path, pointSize, padding, AtlasSizes[atlasIndex], dynamicFont, multiAtlas);
                SelectFont(created); coverage = ProjectFontPipeline.CheckCoverage(scan, targetFont, sourceFont, useGlobalFallbacks);
                status = "新字体已构建，原字体保留。" + coverage.Summary + "；可切到全项目替换页预览应用。";
            });
    }
    void DrawReplacement()
    {
        EditorGUILayout.Space();
        EditorGUI.BeginChangeCheck();
        replacement.TargetTMP = targetFont;
        replacement.TargetLegacy = (Font)EditorGUILayout.ObjectField("目标 Legacy / TextMesh 字体", replacement.TargetLegacy, typeof(Font), false);
        if (sourceFont != null && GUILayout.Button("Legacy 字体使用当前源字体")) replacement.TargetLegacy = sourceFont;
        replacement.OnlyTMP = (TMP_FontAsset)EditorGUILayout.ObjectField("仅替换此 TMP 字体（空=全部）", replacement.OnlyTMP, typeof(TMP_FontAsset), false);
        replacement.OnlyLegacy = (Font)EditorGUILayout.ObjectField("仅替换此 Legacy 字体（空=全部）", replacement.OnlyLegacy, typeof(Font), false);
        replacement.Prefabs = EditorGUILayout.ToggleLeft("Assets 中全部可编辑 prefab（含变体、非激活对象）", replacement.Prefabs);
        replacement.Scenes = EditorGUILayout.ToggleLeft("Assets 中全部场景，不限当前场景", replacement.Scenes);
        replacement.ScriptableObjects = EditorGUILayout.ToggleLeft("ScriptableObject / 配置资产中的字体引用", replacement.ScriptableObjects);
        replacement.DefaultTMPFont = EditorGUILayout.ToggleLeft("同时修改 TMP Settings 默认字体", replacement.DefaultTMPFont);
        replacement.FillMissing = EditorGUILayout.ToggleLeft("为没有指定字体的 TMP / Text / TextMesh 填入目标字体", replacement.FillMissing);
        replacement.ResetMaterial = EditorGUILayout.ToggleLeft("使用目标 TMP 字体默认材质，避免引用旧字库图集", replacement.ResetMaterial);
        if (EditorGUI.EndChangeCheck()) plan = null;
        EditorGUILayout.HelpBox("仅处理 Assets 下明确列入预览的对象，保留 .meta/GUID。跳过模型 prefab、只读/链接文件。已打开的未保存场景只替换编辑器中的字体（支持撤销），不自动保存其他修改；请之后手动保存场景。未打开场景的预览数为依赖候选数。", MessageType.None);
        if (plan == null) return;
        planScroll = EditorGUILayout.BeginScrollView(planScroll, GUILayout.Height(190));
        foreach (var entry in plan.Entries) EditorGUILayout.LabelField($"[{entry.Kind}] {entry.Path}", entry.Count.ToString());
        EditorGUILayout.EndScrollView();
        if (plan.Warnings.Count > 0) EditorGUILayout.HelpBox(string.Join("\n", plan.Warnings.Take(8)), MessageType.Warning);
    }
    void DrawReplacementActions()
    {
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("预览全项目替换范围", GUILayout.Height(34))) Execute(() =>
        {
            plan = ProjectFontPipeline.Preview(replacement);
            status = $"预览：{plan.Entries.Count} 个候选文件，{plan.Count} 个引用/候选依赖；尚未写入。";
        });
        bool ready = plan != null && plan.Entries.Count > 0 && plan.OptionsKey == replacement.Key;
        using (new EditorGUI.DisabledScope(!ready))
            if (GUILayout.Button(ready ? $"备份并替换项目字体（{plan.Entries.Count} 个文件）" : "备份并替换项目字体（请先预览）", GUILayout.Height(34))) Execute(() =>
            {
                var result = ProjectFontPipeline.Apply(plan, replacement); plan = null;
                status = result.Summary + "\n备份和完整报告：" + result.BackupDirectory;
            });
        EditorGUILayout.EndHorizontal();
    }
    void DrawFallbacks()
    {
        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("为当前目标字体配置有序回退链；应用前检查重复、自身引用及循环。缺字检查能区分主字体、回退覆盖和源字体不支持。", MessageType.None);
        for (int i = 0; i < fallbackFonts.Count; i++)
        {
            EditorGUILayout.BeginHorizontal(); fallbackFonts[i] = (TMP_FontAsset)EditorGUILayout.ObjectField("回退 " + (i + 1), fallbackFonts[i], typeof(TMP_FontAsset), false);
            if (GUILayout.Button("上移", GUILayout.Width(46)) && i > 0) { var item = fallbackFonts[i - 1]; fallbackFonts[i - 1] = fallbackFonts[i]; fallbackFonts[i] = item; }
            if (GUILayout.Button("移除", GUILayout.Width(46))) { fallbackFonts.RemoveAt(i); i--; }
            EditorGUILayout.EndHorizontal();
        }
        if (GUILayout.Button("添加回退字体")) fallbackFonts.Add(null);
        changeGlobalDefault = EditorGUILayout.ToggleLeft("同时将此字体设为 TMP 全局默认字体", changeGlobalDefault);
        using (new EditorGUI.DisabledScope(targetFont == null))
            if (GUILayout.Button("备份并保存回退设置", GUILayout.Height(32))) Execute(() => { string backup = ProjectFontPipeline.ApplyFallbacks(targetFont, fallbackFonts, changeGlobalDefault); coverage = null; plan = null; status = "回退设置已保存，备份：" + backup; });
        if (GUILayout.Button("检查项目文字覆盖率")) InspectCoverage();
        DrawCoverage();
    }
}
