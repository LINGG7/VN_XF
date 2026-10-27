#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

// Writes only the requested history prefabs; reuses the existing shared font without modifying it.
[InitializeOnLoad]
public static class CodexHistoryPanelDesigner
{
    const string PanelPath="Assets/Resources/VNovelizerRes/VNPrefabs/UI/History/HistoryPanel.prefab";
    const string ItemPath="Assets/Resources/VNovelizerRes/VNPrefabs/UI/History/HistoryItem.prefab";
    const string FontPath="Assets/TextMesh Pro/Resources/Fonts & Materials/ChillKai SDF 2048 v2.asset";
    const string OwnSource="Assets/Editor/CodexHistoryPanelDesigner.cs";
    const string Version="5";
    static readonly Color Ice=new Color(.66f,.87f,1f,1f);
    static readonly Color White=new Color(.91f,.96f,1f,1f);
    static double nextCheck;
    static long sourceTicks=File.GetLastWriteTimeUtc(OwnSource).Ticks;
    static IEnumerator preview;
    static string activeToken;
    static TMP_FontAsset font;
    static string Output=>Path.Combine(Directory.GetParent(Application.dataPath).FullName,"output/HistoryPanelRedesign");
    [Serializable] class Report { public string version; public string token; public string status; public string detail; }
    static CodexHistoryPanelDesigner() { EditorApplication.update+=Tick; }
    static void Tick()
    {
        if(EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
        if(preview!=null) {
            try { if(!preview.MoveNext()) { preview=null; Result("complete","Both prefabs saved and reloaded. No missing scripts, no timestamp fields, dynamic card and scroll checks passed. Previews rendered."); } }
            catch(Exception ex) { preview=null; Result("failed",ex.ToString()); Debug.LogException(ex); }
            return;
        }
        if(EditorApplication.timeSinceStartup<nextCheck) return;
        nextCheck=EditorApplication.timeSinceStartup+1;
        long ticks=File.GetLastWriteTimeUtc(OwnSource).Ticks;
        if(ticks!=sourceTicks) { sourceTicks=ticks; AssetDatabase.ImportAsset(OwnSource,ImportAssetOptions.ForceUpdate); UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation(); return; }
        string request=Path.Combine(Output,"request.txt");
        if(!File.Exists(request)) return;
        string token=File.ReadAllText(request).Trim();
        if(string.IsNullOrEmpty(token) || token==activeToken) return;
        string report=Path.Combine(Output,"result.json");
        if(File.Exists(report)) { var previous=JsonUtility.FromJson<Report>(File.ReadAllText(report)); if(previous!=null && previous.token==token && previous.version==Version) return; }
        if(SessionState.GetString("CodexHistoryDesign."+Version,"")==token) return;
        activeToken=token; SessionState.SetString("CodexHistoryDesign."+Version,token);
        try { font=LoadHistoryFont(); DesignItem(); DesignPanel(); ValidateSaved(); preview=RenderPreviews(); }
        catch(Exception ex) { Result("failed",ex.ToString()); Debug.LogException(ex); }
    }
    static void Result(string status,string detail) { File.WriteAllText(Path.Combine(Output,"result.json"),JsonUtility.ToJson(new Report {version=Version,token=activeToken,status=status,detail=detail},true)); }
    static void Verify(string path)
    {
        string project=Directory.GetParent(Application.dataPath).FullName;
        string full=Path.GetFullPath(Path.Combine(project,path));
        if(!full.StartsWith(project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new Exception("Outside project: "+path);
        for(FileSystemInfo item=File.Exists(full)?(FileSystemInfo)new FileInfo(full):new DirectoryInfo(full);item!=null;item=item is FileInfo f?f.Directory:((DirectoryInfo)item).Parent)
            if(item.Attributes!=(FileAttributes)(-1) && (item.Attributes&FileAttributes.ReparsePoint)!=0) throw new Exception("Linked path refused: "+item.FullName);
    }
    static Transform Find(Transform root,string name) {
        if(root.name==name) return root;
        foreach(Transform child in root) { var found=Find(child,name); if(found!=null) return found; }
        return null;
    }
    static Transform Need(Transform root,string name)=>Find(root,name)??throw new Exception("Missing: "+name);
    static RectTransform Stretch(Transform t,float left=0,float bottom=0,float right=0,float top=0) {
        var r=(RectTransform)t; r.anchorMin=Vector2.zero; r.anchorMax=Vector2.one; r.pivot=new Vector2(.5f,.5f); r.offsetMin=new Vector2(left,bottom); r.offsetMax=new Vector2(-right,-top); r.localScale=Vector3.one; r.localRotation=Quaternion.identity; return r;
    }
    static RectTransform Top(Transform t,float x,float y,float w,float h) {
        var r=(RectTransform)t; r.anchorMin=r.anchorMax=new Vector2(0,1); r.pivot=new Vector2(0,1); r.anchoredPosition=new Vector2(x,-y); r.sizeDelta=new Vector2(w,h); r.localScale=Vector3.one; r.localRotation=Quaternion.identity; return r;
    }
    static Transform Child(Transform parent,string name,Type component) {
        var child=parent.Find(name); if(child!=null) { if(child.GetComponent<CanvasRenderer>()==null) child.gameObject.AddComponent<CanvasRenderer>(); return child; }
        var go=new GameObject(name,typeof(RectTransform),typeof(CanvasRenderer),component); go.layer=5; go.transform.SetParent(parent,false); return go.transform;
    }
    static Sprite Art(string name) { string path="Assets/ArtResources/UI_New/"+name; Verify(path); return AssetDatabase.LoadAssetAtPath<Sprite>(path)??throw new Exception("Missing sprite "+name); }
    static Image Image(Transform parent,string name,Color color) { var t=Child(parent,name,typeof(Image)); var i=t.GetComponent<Image>(); i.color=color; i.sprite=null; i.raycastTarget=false; return i; }
    static TextMeshProUGUI Text(Transform parent,string name,string text,float size,Color color) {
        var t=Child(parent,name,typeof(TextMeshProUGUI)); var label=t.GetComponent<TextMeshProUGUI>(); label.font=font; label.text=text; label.fontSize=size; label.enableAutoSizing=false; label.color=color; label.margin=Vector4.zero; label.enableWordWrapping=false; label.raycastTarget=false; label.fontStyle=FontStyles.Normal; return label;
    }
    static void DisableLayouts(Transform root) {
        foreach(var layout in root.GetComponentsInChildren<LayoutGroup>(true)) layout.enabled=false;
        foreach(var fitter in root.GetComponentsInChildren<ContentSizeFitter>(true)) fitter.enabled=false;
    }
    static TMP_FontAsset LoadHistoryFont()
    {
        Verify(FontPath);
        return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath)
            ?? throw new Exception("Requested font is missing: " + FontPath);
    }

    static void DesignItem()
    {
        Verify(ItemPath); var root=PrefabUtility.LoadPrefabContents(ItemPath);
        try {

            DisableLayouts(root.transform); foreach(var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer=5;
            var r=(RectTransform)root.transform; r.anchorMin=new Vector2(0,1); r.anchorMax=Vector2.one; r.pivot=new Vector2(.5f,1); r.anchoredPosition=Vector2.zero; r.sizeDelta=new Vector2(0,156);
            var layout=root.GetComponent<LayoutElement>()??root.AddComponent<LayoutElement>(); layout.ignoreLayout=false; layout.minHeight=156; layout.preferredHeight=156; layout.flexibleHeight=0;
            var speaker=Need(root.transform,"H_SpeakerBox"); speaker.gameObject.SetActive(true); Top(speaker,62,24,238,68);
            var nameImage=Need(speaker,"H_SpeakerBoxImage").GetComponent<Image>(); Stretch(nameImage.transform); nameImage.sprite=Art("namebox.png"); nameImage.type=UnityEngine.UI.Image.Type.Simple; nameImage.color=Color.white; nameImage.raycastTarget=false; nameImage.transform.SetAsFirstSibling();
            var speakerText=Need(speaker,"H_SpeakerText").GetComponent<TextMeshProUGUI>(); Stretch(speakerText.transform,31,5,18,5); speakerText.font=font; speakerText.text="旁白"; speakerText.fontSize=32; speakerText.color=White; speakerText.alignment=TextAlignmentOptions.MidlineLeft; speakerText.enableAutoSizing=true; speakerText.fontSizeMin=18; speakerText.fontSizeMax=32; speakerText.enableWordWrapping=false; speakerText.raycastTarget=false;
            var node=Child(root.transform,"HistoryTimelineNode",typeof(HistoryGlassGraphic)); Top(node,2,38,36,36); node.GetComponent<HistoryGlassGraphic>().shape=HistoryGlassGraphic.Shape.TimelineNode; node.GetComponent<HistoryGlassGraphic>().raycastTarget=false;
            var content=Need(root.transform,"H_Content"); Stretch(content,330,0,26,0);
            var dialogueBox=Need(content,"H_DialogueBox"); Stretch(dialogueBox);
            var oldImage=dialogueBox.GetComponent<Image>(); if(oldImage!=null) oldImage.enabled=false;
            var glass=Child(dialogueBox,"HistoryGlassSurface",typeof(HistoryGlassGraphic)); Stretch(glass); glass.SetAsFirstSibling(); var graphic=glass.GetComponent<HistoryGlassGraphic>(); graphic.shape=HistoryGlassGraphic.Shape.Dialogue; graphic.raycastTarget=false; graphic.SetAllDirty();
            var dialogue=Need(dialogueBox,"H_Dialogue").GetComponent<TextMeshProUGUI>(); Stretch(dialogue.transform,40,25,38,25); dialogue.font=font; dialogue.text=""; dialogue.fontSize=30; dialogue.fontSizeMin=30; dialogue.fontSizeMax=30; dialogue.enableAutoSizing=false; dialogue.color=new Color(.025f,.055f,.085f,1); dialogue.alignment=TextAlignmentOptions.TopLeft; dialogue.enableWordWrapping=true; dialogue.overflowMode=TextOverflowModes.Overflow; dialogue.margin=Vector4.zero; dialogue.lineSpacing=7; dialogue.raycastTarget=false;
            Need(content,"H_Replay").gameObject.SetActive(false);
            var view=root.GetComponent<HistoryItemView>()??root.AddComponent<HistoryItemView>(); var serialized=new SerializedObject(view); serialized.FindProperty("speakerLabel").objectReferenceValue=speakerText; serialized.FindProperty("dialogueLabel").objectReferenceValue=dialogue; serialized.FindProperty("itemLayout").objectReferenceValue=layout; serialized.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root,ItemPath,out bool saved); if(!saved) throw new Exception("Could not save history item.");
        } finally { PrefabUtility.UnloadPrefabContents(root); }
    }
    static void DesignPanel()
    {
        Verify(PanelPath); var root=PrefabUtility.LoadPrefabContents(PanelPath);
        try {
            Stretch(root.transform); foreach(var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer=5;
            var background=root.GetComponent<Image>(); background.sprite=null; background.color=new Color(.025f,.045f,.065f,.82f); background.raycastTarget=true;
            var close=Need(root.transform,"H_Close"); Top(close,42,28,92,76);
            var closeImage=close.GetComponent<Image>(); closeImage.sprite=Art("back.png"); closeImage.type=UnityEngine.UI.Image.Type.Simple; closeImage.preserveAspect=true; closeImage.color=Color.white;
            foreach(var label in close.GetComponentsInChildren<TextMeshProUGUI>(true)) label.gameObject.SetActive(false);
            close.GetComponent<Button>().targetGraphic=closeImage;
            var heading=Text(root.transform,"HistoryHeading","历史对话",43,White); Top(heading.transform,174,27,270,64); heading.alignment=TextAlignmentOptions.MidlineLeft;
            var english=Text(root.transform,"HistoryHeadingEnglish","History",23,new Color(.38f,.6f,.75f,.65f)); Top(english.transform,436,52,180,35); english.fontStyle=FontStyles.Italic;
            var rule=Image(root.transform,"HistoryHeaderRule",new Color(.65f,.83f,.95f,.6f)); var line=Stretch(rule.transform,166,0,62,0); line.anchorMin=new Vector2(0,1); line.anchorMax=Vector2.one; line.sizeDelta=new Vector2(-228,1); line.anchoredPosition=new Vector2(52,-109);
            var scroll=Need(root.transform,"H_Scroll View").GetComponent<ScrollRect>(); Stretch(scroll.transform,106,100,84,176); scroll.horizontal=false; scroll.vertical=true; scroll.movementType=ScrollRect.MovementType.Clamped; scroll.inertia=true; scroll.scrollSensitivity=28;
            var scrollImage=scroll.GetComponent<Image>(); if(scrollImage!=null) { scrollImage.sprite=null; scrollImage.color=Color.clear; scrollImage.raycastTarget=true; }
            var viewport=scroll.viewport; Stretch(viewport,0,0,32,0);
            var mask=viewport.GetComponent<Mask>(); if(mask!=null) mask.enabled=false;
            if(viewport.GetComponent<RectMask2D>()==null) viewport.gameObject.AddComponent<RectMask2D>();
            var viewportImage=viewport.GetComponent<Image>(); if(viewportImage!=null) { viewportImage.color=Color.clear; viewportImage.raycastTarget=true; }
            var content=scroll.content; content.anchorMin=new Vector2(0,1); content.anchorMax=Vector2.one; content.pivot=new Vector2(.5f,1); content.anchoredPosition=Vector2.zero; content.sizeDelta=Vector2.zero;
            var list=content.GetComponent<VerticalLayoutGroup>()??content.gameObject.AddComponent<VerticalLayoutGroup>(); list.enabled=true; list.padding=new RectOffset(0,0,18,22); list.spacing=38; list.childControlWidth=true; list.childControlHeight=true; list.childForceExpandWidth=true; list.childForceExpandHeight=false; list.childAlignment=TextAnchor.UpperCenter;
            var fitter=content.GetComponent<ContentSizeFitter>()??content.gameObject.AddComponent<ContentSizeFitter>(); fitter.enabled=true; fitter.horizontalFit=ContentSizeFitter.FitMode.Unconstrained; fitter.verticalFit=ContentSizeFitter.FitMode.PreferredSize;
            var timeline=Image(viewport,"HistoryTimelineRule",new Color(.47f,.75f,.94f,.55f)); timeline.transform.SetAsFirstSibling(); var tl=(RectTransform)timeline.transform; tl.anchorMin=new Vector2(0,0); tl.anchorMax=new Vector2(0,1); tl.pivot=new Vector2(.5f,.5f); tl.sizeDelta=new Vector2(1,0); tl.anchoredPosition=new Vector2(20,0);
            if(scroll.horizontalScrollbar!=null) scroll.horizontalScrollbar.gameObject.SetActive(false); scroll.horizontalScrollbar=null;
            var bar=scroll.verticalScrollbar; bar.gameObject.SetActive(true); var br=(RectTransform)bar.transform; br.anchorMin=new Vector2(1,0); br.anchorMax=Vector2.one; br.pivot=new Vector2(1,.5f); br.offsetMin=new Vector2(-12,12); br.offsetMax=new Vector2(0,-12);
            var track=bar.GetComponent<Image>(); track.color=new Color(.6f,.72f,.85f,.25f); track.raycastTarget=true;
            Stretch(bar.handleRect.parent,2,2,2,2); var thumb=bar.handleRect.GetComponent<Image>(); thumb.color=new Color(.76f,.91f,1f,1); thumb.raycastTarget=true; bar.targetGraphic=thumb; bar.direction=Scrollbar.Direction.BottomToTop;
            bar.handleRect.sizeDelta=Vector2.zero; bar.size=.5f; bar.SetValueWithoutNotify(1f); var barColors=bar.colors; barColors.normalColor=Color.white; barColors.highlightedColor=Color.white; barColors.selectedColor=Color.white; bar.colors=barColors;
            scroll.verticalScrollbarVisibility=ScrollRect.ScrollbarVisibility.Permanent;
            var empty=Text(root.transform,"HistoryEmptyState","暂无历史对话",30,new Color(.65f,.79f,.88f,1)); Stretch(empty.transform,400,250,240,250); empty.alignment=TextAlignmentOptions.Center; empty.gameObject.SetActive(false);
            if(content.childCount!=0) throw new Exception("History content contains serialized rows; refusing to remove user objects.");
            PrefabUtility.SaveAsPrefabAsset(root,PanelPath,out bool saved); if(!saved) throw new Exception("Could not save history panel.");
        } finally { PrefabUtility.UnloadPrefabContents(root); }
    }
    static void ValidateSaved()
    {
        AssetDatabase.ImportAsset(ItemPath,ImportAssetOptions.ForceSynchronousImport); AssetDatabase.ImportAsset(PanelPath,ImportAssetOptions.ForceSynchronousImport);
        foreach(string path in new[]{ItemPath,PanelPath}) {
            var root=AssetDatabase.LoadAssetAtPath<GameObject>(path); if(root==null) throw new Exception("Reload failed: "+path);
            foreach(var t in root.GetComponentsInChildren<Transform>(true)) if(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject)!=0) throw new Exception("Missing script "+t.name);
            foreach(var graphic in root.GetComponentsInChildren<Graphic>(true)) if(graphic.GetComponent<CanvasRenderer>()==null) throw new Exception("Missing canvas renderer "+graphic.name);
            foreach(var label in root.GetComponentsInChildren<TextMeshProUGUI>(true)) if(label.font!=font) throw new Exception("History text must use ChillKai SDF 2048 v2: "+label.name);
        }
        var panel=AssetDatabase.LoadAssetAtPath<GameObject>(PanelPath); var scroll=panel.GetComponentInChildren<ScrollRect>(true);
        if(scroll==null || scroll.viewport==null || scroll.content==null || scroll.verticalScrollbar==null) throw new Exception("Broken scroll references.");
        if(AssetDatabase.LoadAssetAtPath<GameObject>(ItemPath).GetComponent<HistoryItemView>()==null) throw new Exception("Item view missing.");
        if(typeof(HistoryEntry).GetFields().Length!=3) throw new Exception("Unexpected change to history data fields.");
    }
    static IEnumerator RenderPreviews()
    {
        var root=PrefabUtility.LoadPrefabContents(PanelPath); RenderTexture target=null;
        try {
            var scene=root.scene; EditorSceneManager.SetSceneCullingMask(scene,1UL<<63);
            var cameraObject=new GameObject("HistoryPreviewCamera",typeof(Camera)); SceneManager.MoveGameObjectToScene(cameraObject,scene); var camera=cameraObject.GetComponent<Camera>(); camera.scene=scene; camera.overrideSceneCullingMask=EditorSceneManager.GetSceneCullingMask(scene); camera.transform.position=new Vector3(0,0,-100); camera.orthographic=true; camera.orthographicSize=540; camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(.065f,.105f,.14f,1); camera.cullingMask=1<<5;
            var canvasObject=new GameObject("HistoryPreviewCanvas",typeof(RectTransform),typeof(Canvas)); canvasObject.layer=5; SceneManager.MoveGameObjectToScene(canvasObject,scene); var canvas=canvasObject.GetComponent<Canvas>(); canvas.renderMode=RenderMode.WorldSpace;
            var cr=(RectTransform)canvasObject.transform; cr.sizeDelta=new Vector2(1920,1080); root.transform.SetParent(canvasObject.transform,false); Stretch(root.transform);
            var scroll=root.GetComponentInChildren<ScrollRect>(true);
            string[] speakers={"旁白","英杰","你","旁白","英杰","你"};
            string[] texts={"研一时那个充满探究欲和科研精神的年轻人早已被扼杀在了无穷无尽的论文指标和项目考核里。现在你只想尽早回到那间不足40平米的学区公寓，填饱早就叫的肚子，再仰面朝天地补上昨夜缺失的睡眠。","那晚点的社团例会你还来不来，社长说今天有什么重要事宜要宣布。实在实在需要休息的话我就帮你请个假。","那我还是去吧，宿舍学校也就几步路，回去休息会儿就过来。","窗外的光落在空荡的走廊上，远处传来隐约的人声。","嗯，那就晚点见。","晚点见。"};
            foreach(int index in Enumerable.Range(0,speakers.Length)) { var item=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ItemPath),scene); item.transform.SetParent(scroll.content,false); item.GetComponent<HistoryItemView>().Bind(new HistoryEntry(speakers[index],texts[index])); }
            EditorApplication.QueuePlayerLoopUpdate(); yield return null; yield return null;
            foreach(int height in new[]{866,1080}) {
                cr.sizeDelta=new Vector2(1920,height); camera.orthographicSize=height*.5f;
                target=new RenderTexture(1920,height,24); target.Create(); camera.targetTexture=target;
                for(int frame=0;frame<3;frame++) { Canvas.ForceUpdateCanvases(); foreach(var item in root.GetComponentsInChildren<HistoryItemView>(true)) item.RefreshHeight(); LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content); EditorApplication.QueuePlayerLoopUpdate(); yield return null; }
                scroll.verticalNormalizedPosition=1;
                Canvas.ForceUpdateCanvases(); foreach(var text in root.GetComponentsInChildren<TextMeshProUGUI>()) text.ForceMeshUpdate(true,true); yield return null;
                camera.Render(); var previous=RenderTexture.active; RenderTexture.active=target; var png=new Texture2D(1920,height,TextureFormat.RGB24,false); png.ReadPixels(new Rect(0,0,1920,height),0,0); png.Apply(); RenderTexture.active=previous;
                if(png.GetPixels32().Where((pixel,index)=>index%103==0).Distinct().Count()<16) throw new Exception("Blank preview.");
                File.WriteAllBytes(Path.Combine(Output,"History-1920x"+height+".png"),png.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(png);
                camera.targetTexture=null; target.Release(); UnityEngine.Object.DestroyImmediate(target); target=null;
            }
            var views=root.GetComponentsInChildren<HistoryItemView>(true); var firstLayout=views[0].GetComponent<LayoutElement>(); float originalHeight=firstLayout.preferredHeight;
            views[0].Bind(new HistoryEntry("同一角色",string.Concat(Enumerable.Repeat("这是一段用于检查长对话自动换行和高度的测试文字。",25))));
            Canvas.ForceUpdateCanvases(); views[0].RefreshHeight(); LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content); yield return null;
            if(firstLayout.preferredHeight<=originalHeight) throw new Exception("Long dialogue did not expand.");
            float expandedHeight=firstLayout.preferredHeight;
            views[0].Bind(new HistoryEntry("","短句。")); if(Need(views[0].transform,"H_SpeakerText").GetComponent<TMP_Text>().text!="旁白") throw new Exception("Narration fallback failed.");
            if(firstLayout.preferredHeight>=expandedHeight) throw new Exception("Pooled row did not shrink.");
            LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content); Canvas.ForceUpdateCanvases(); yield return null;
            scroll.StopMovement(); scroll.verticalNormalizedPosition=0; Canvas.ForceUpdateCanvases(); yield return null;
            File.WriteAllText(Path.Combine(Output,"diagnostics.txt"),"font="+font.name+" source="+AssetDatabase.GetAssetPath(font.sourceFontFile)+" mode="+font.atlasPopulationMode+" titleGlyphs="+string.Join(",","历史对话隐约".Select(c=>c+":"+font.HasCharacter(c)))+" content="+scroll.content.rect+" viewport="+scroll.viewport.rect+" normalized="+scroll.verticalNormalizedPosition+" barSize="+scroll.verticalScrollbar.size+" handle="+scroll.verticalScrollbar.handleRect.rect+" handleActive="+scroll.verticalScrollbar.handleRect.gameObject.activeInHierarchy);
            if(Mathf.Abs(scroll.verticalNormalizedPosition)>.01f) throw new Exception("Scroll-to-bottom failed.");
        } finally { if(target!=null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); } PrefabUtility.UnloadPrefabContents(root); }
    }
}
#endif