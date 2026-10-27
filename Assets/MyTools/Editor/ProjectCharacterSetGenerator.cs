using UnityEditor;
using UnityEngine;

// Retained so existing editor calls/windows still resolve; no automatic destructive rebuild.
public class ProjectCharacterSetGenerator : EditorWindow
{
    public static void ShowWindow() => ProjectFontManagerWindow.Open();
    public static void GenerateWithDefaults() => ProjectFontManagerWindow.Open().ScanAll();
    public static void ScanAndRebuildFontAssetNow()
    {
        // Prepare the scan; building/supplementing is explicitly chosen in the unified window.
        ProjectFontManagerWindow.Open(1).ScanAll();
    }
    void OnGUI()
    {
        EditorGUILayout.HelpBox("字符集、增补和重建已整合到 Tools / VNovelizer / 字体管理工具。", MessageType.Info);
        if (GUILayout.Button("打开统一字体管理工具")) ShowWindow();
    }
}