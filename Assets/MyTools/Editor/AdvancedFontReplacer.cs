using TMPro;
using UnityEditor;
using UnityEngine;

// Compatibility wrapper. All font operations now live in ProjectFontManagerWindow.
public class AdvancedFontReplacer : EditorWindow
{
    public TMP_FontAsset newTMPFont;
    public Font newLegacyFont;
    public static void ShowWindow() => ProjectFontManagerWindow.Open(2);
    void OnGUI()
    {
        EditorGUILayout.HelpBox("已整合到 Tools / VNovelizer / 字体管理工具。", MessageType.Info);
        if (GUILayout.Button("打开统一字体管理工具")) ShowWindow();
    }
}