#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.SceneManagement;

// Edits only the explicitly requested SettingsPanel prefab in this project.
// Never creates projects, links, deletes assets, saves scenes or saves unrelated assets.
[InitializeOnLoad]
public static class CodexSettingsPanelDesigner
{
    public const string Target = "Assets/Resources/VNovelizerRes/VNPrefabs/UI/Settings/SettingsPanel.prefab";
    const string ToolVersion = "7";
    const string Art = "Assets/ArtResources/UI_New/";
    static readonly Color Cyan = new Color(.55f,.85f,1f,1f);
    static readonly Color White = new Color(.89f,.95f,1f,1f);
    static readonly Color Muted = new Color(.44f,.61f,.73f,1f);
    static readonly Color Track = new Color(.08f,.16f,.23f,1f);
    static readonly Color Fill = new Color(.29f,.68f,.91f,1f);
    const string OwnSource = "Assets/Editor/CodexSettingsPanelDesigner.cs";
    static long sourceTicks = File.GetLastWriteTimeUtc(OwnSource).Ticks;
    static double nextCheck;
    static string lastToken;
    static IEnumerator previewRoutine;
    static string previewToken;
    static TMP_FontAsset font;
    static string Output => Path.Combine(Directory.GetParent(Application.dataPath).FullName,"output/SettingsPanelRedesign");
    [Serializable] class Result { public string version; public string token; public string status; public string prefab; public string detail; }

    static CodexSettingsPanelDesigner() { EditorApplication.update += CheckRequest; }
    static void CheckRequest()
    {
        if(previewRoutine!=null) {
            try { if(!previewRoutine.MoveNext()) { previewRoutine=null; WriteResult(previewToken,"complete","Saved with PrefabUtility, reloaded and validated all controls; previews rendered."); } }
            catch(Exception ex) { previewRoutine=null; WriteResult(previewToken,"failed",ex.ToString()); Debug.LogException(ex); }
            return;
        }
        if (EditorApplication.timeSinceStartup < nextCheck || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
        nextCheck = EditorApplication.timeSinceStartup + 1;
        long currentSourceTicks=File.GetLastWriteTimeUtc(OwnSource).Ticks;
        if(currentSourceTicks!=sourceTicks) { sourceTicks=currentSourceTicks; AssetDatabase.ImportAsset(OwnSource,ImportAssetOptions.ForceUpdate); UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation(); return; }
        string request = Path.Combine(Output,"request.txt");
        if (!File.Exists(request)) return;
        string token = File.ReadAllText(request).Trim();
        if (string.IsNullOrEmpty(token) || token == lastToken || SessionState.GetString("CodexSettingsPanelDesigner.lastToken."+ToolVersion,"") == token) return;
        string report=Path.Combine(Output,"result.json");
        if(File.Exists(report)) { var previous=JsonUtility.FromJson<Result>(File.ReadAllText(report)); if(previous!=null && previous.token==token && previous.version==ToolVersion) return; }
        lastToken = token;
        SessionState.SetString("CodexSettingsPanelDesigner.lastToken."+ToolVersion,token);
        try {
            Redesign();
            previewToken=token; previewRoutine=RenderPreviews();
        } catch (Exception ex) {
            WriteResult(token,"failed",ex.ToString());
            Debug.LogException(ex);
        }
    }
    static void WriteResult(string token,string status,string detail) {
        File.WriteAllText(Path.Combine(Output,"result.json"),JsonUtility.ToJson(new Result { version=ToolVersion,token=token,status=status,prefab=Target,detail=detail },true));
    }
    static void VerifyPath(string relative)
    {
        string project = Directory.GetParent(Application.dataPath).FullName;
        string full = Path.GetFullPath(Path.Combine(project,relative));
        if (!full.StartsWith(project + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new Exception("Path outside project: " + relative);
        for (FileSystemInfo item = File.Exists(full) ? (FileSystemInfo)new FileInfo(full) : new DirectoryInfo(full); item != null; item = item is FileInfo f ? f.Directory : ((DirectoryInfo)item).Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new Exception("Linked path refused: " + item.FullName);
    }
    static Transform Find(Transform root,string name) {
        if(root.name==name) return root;
        foreach(Transform child in root) { var found=Find(child,name); if(found!=null) return found; }
        return null;
    }
    static Transform Need(Transform root,string name) => Find(root,name) ?? throw new Exception("Missing control: " + name);
    static Sprite Sprite(string name) {
        VerifyPath(Art+name);
        return AssetDatabase.LoadAssetAtPath<Sprite>(Art+name) ?? throw new Exception("Missing sprite: " + name);
    }
    static RectTransform Rect(Transform t,float x,float y,float w,float h) {
        var r=(RectTransform)t;
        r.anchorMin=r.anchorMax=r.pivot=new Vector2(.5f,.5f);
        r.anchoredPosition=new Vector2(x,y); r.sizeDelta=new Vector2(w,h); r.localScale=Vector3.one; r.localRotation=Quaternion.identity;
        return r;
    }
    static RectTransform Stretch(Transform t,float left=0,float bottom=0,float right=0,float top=0) {
        var r=(RectTransform)t; r.anchorMin=Vector2.zero; r.anchorMax=Vector2.one; r.pivot=new Vector2(.5f,.5f);
        r.offsetMin=new Vector2(left,bottom); r.offsetMax=new Vector2(-right,-top); r.localScale=Vector3.one;
        return r;
    }
    static Transform Child(Transform parent,string name,Type component) {
        Transform t=parent.Find(name);
        if(t==null) { var go=new GameObject(name,typeof(RectTransform),component); go.layer=5; go.transform.SetParent(parent,false); t=go.transform; }
        return t;
    }
    static Image Image(Transform parent,string name,Sprite sprite,Color color,float x,float y,float w,float h) {
        Transform t=Child(parent,name,typeof(Image)); Rect(t,x,y,w,h);
        var image=t.GetComponent<Image>(); image.sprite=sprite; image.color=color; image.type=UnityEngine.UI.Image.Type.Simple; image.raycastTarget=false;
        return image;
    }
    static TextMeshProUGUI Text(Transform parent,string name,string text,float size,Color color,float x,float y,float w,float h) {
        Transform t=Child(parent,name,typeof(TextMeshProUGUI)); Rect(t,x,y,w,h);
        var label=t.GetComponent<TextMeshProUGUI>(); label.font=font; label.text=text; label.fontSize=size; label.enableAutoSizing=false; label.fontStyle=FontStyles.Normal;
        label.color=color; label.alignment=TextAlignmentOptions.MidlineLeft; label.enableWordWrapping=false; label.overflowMode=TextOverflowModes.Ellipsis; label.raycastTarget=false; label.margin=Vector4.zero;
        return label;
    }
    static void DisableLayout(Transform t) {
        foreach(var layout in t.GetComponents<LayoutGroup>()) layout.enabled=false;
        foreach(var fitter in t.GetComponents<ContentSizeFitter>()) fitter.enabled=false;
    }
    static void StyleText(TextMeshProUGUI label,float size,Color color) {
        label.font=font; label.fontSize=size; label.enableAutoSizing=false; label.color=color; label.enableWordWrapping=false; label.raycastTarget=false; label.fontStyle=FontStyles.Normal;
    }
    static void Redesign()
    {
        VerifyPath(Target); VerifyPath("output/SettingsPanelRedesign");
        if (!File.Exists(Target)) throw new Exception("The explicitly approved existing prefab is missing.");
        GameObject root=PrefabUtility.LoadPrefabContents(Target);
        try {
            var settings=root.GetComponent<SettingsPanel>();
            if(settings==null) throw new Exception("SettingsPanel component missing.");
            font=root.GetComponentsInChildren<TextMeshProUGUI>(true).Select(t=>t.font).FirstOrDefault(f=>f!=null);
            if(font==null) throw new Exception("Original font missing.");
            Rect(root.transform,0,0,1600,820);
            var content=Need(root.transform,"ContentArea"); Stretch(content); DisableLayout(content);
            var background=Need(root.transform,"Background").GetComponent<Image>();
            if(background.transform.parent!=root.transform) throw new Exception("Unexpected background hierarchy.");
            Stretch(background.transform); background.sprite=Sprite("saveloadbg.jpg"); background.type=UnityEngine.UI.Image.Type.Simple; background.color=Color.white; background.raycastTarget=true; background.transform.SetAsFirstSibling();
            var tint=Image(root.transform,"SettingsBackdropTint",null,new Color(.015f,.04f,.065f,.75f),0,0,1600,820); tint.transform.SetSiblingIndex(1);
            // Thin frame and quiet surfaces keep the supplied film background readable.
            var surface=Image(root.transform,"SettingsContentSurface",null,new Color(.018f,.043f,.072f,.88f),182,-18,1092,606); surface.transform.SetSiblingIndex(2);
            var sidebar=Image(root.transform,"SettingsSidebarSurface",null,new Color(.012f,.032f,.052f,.9f),-581,-18,318,606); sidebar.transform.SetSiblingIndex(3);
            Image(root.transform,"SettingsTopLine",null,Fill,0,408,1600,2);
            Image(root.transform,"SettingsBottomLine",null,new Color(.24f,.48f,.63f,.6f),0,-408,1600,2);
            Image(root.transform,"SettingsHeaderRule",null,new Color(.3f,.57f,.72f,.5f),0,278,1480,1);
            Image(root.transform,"SettingsSidebarRule",null,new Color(.25f,.51f,.66f,.4f),-398,-18,1,606);
            foreach(var oldTitle in root.GetComponentsInChildren<TextMeshProUGUI>(true).Where(t=>t.name=="Title")) oldTitle.gameObject.SetActive(false);
            Image(root.transform,"SettingsHeaderIcon",Sprite("setting.png"),Color.white,-706,343,56,60);
            Text(root.transform,"SettingsHeading","系统设置",44,White,-498,347,310,72).overflowMode=TextOverflowModes.Overflow;
            Text(root.transform,"SettingsHeadingEnglish","SETTINGS",15,Muted,-497,305,308,24);
            Text(root.transform,"SettingsFooter","设置更改后自动保存",19,Muted,-493,-365,490,30);
            Text(root.transform,"SettingsFooterHint","点击面板外侧可返回",17,Muted,524,-365,400,30).alignment=TextAlignmentOptions.MidlineRight;
            Text(root.transform,"SettingsCategoryCaption","设置分类",18,Muted,-581,230,244,30);
            Text(root.transform,"SettingsSidebarNote","调整游戏体验",18,Muted,-581,-244,244,30);
            var tabs=Need(root.transform,"TabsButtons"); DisableLayout(tabs); Rect(tabs,-581,0,280,410);
            StyleTab(Need(tabs,"VolumePageBtn"),"声音","01",144,true);
            StyleTab(Need(tabs,"TextPageBtn"),"文本","02",32,false);
            StyleTab(Need(tabs,"DisplayPageBtn"),"显示","03",-80,false);
            StylePage(Need(content,"VolumePage"),"声音设置","调节游戏中的各类声音",new[]{"MasterVolumeGroup","BGMVolumeGroup","SFXVolumeGroup","VoiceVolumeGroup"},new[]{"主音量","背景音乐","音效音量","语音音量"},new[]{settings.enableMasterVolume,settings.enableBGMVolume,settings.enableSFXVolume,settings.enableVoiceVolume},true);
            StylePage(Need(content,"TextPage"),"文本设置","按你的阅读节奏调整文字与自动播放",new[]{"TextSpeedGroup","AutoSpeedGroup"},new[]{"文字显示速度","自动播放间隔"},new[]{settings.enableTextSpeed,settings.enableAutoSpeed},false);
            StyleDisplayPage(Need(content,"DisplayPage"),settings);
            StyleClose(Need(root.transform,"CloseBtn"));
            var serialized=new SerializedObject(settings);
            serialized.FindProperty("selectedTabBackgroundColor").colorValue=new Color(.75f,.9f,1f,1f);
            serialized.FindProperty("normalTabBackgroundColor").colorValue=new Color(.4f,.55f,.65f,.85f);
            serialized.FindProperty("selectedTabTextColor").colorValue=Cyan;
            serialized.FindProperty("normalTabTextColor").colorValue=new Color(.55f,.65f,.72f,1f);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Validate(root);
            PrefabUtility.SaveAsPrefabAsset(root,Target,out bool saved);
            if(!saved) throw new Exception("Prefab save failed.");
        } finally { PrefabUtility.UnloadPrefabContents(root); }
        AssetDatabase.ImportAsset(Target,ImportAssetOptions.ForceSynchronousImport);
        var reloaded=AssetDatabase.LoadAssetAtPath<GameObject>(Target);
        Validate(reloaded);

    }
    static void StyleTab(Transform tab,string title,string number,float y,bool selected)
    {
        Rect(tab,0,y,274,88); DisableLayout(tab);
        var image=tab.GetComponent<Image>(); image.sprite=Sprite("namebox.png"); image.type=UnityEngine.UI.Image.Type.Simple; image.color=selected?new Color(.19f,.43f,.58f,1):new Color(.4f,.55f,.65f,.85f); image.raycastTarget=true;
        var button=tab.GetComponent<Button>(); button.targetGraphic=image; button.transition=Selectable.Transition.ColorTint;
        var colors=button.colors; colors.normalColor=Color.white; colors.highlightedColor=new Color(.75f,.91f,1); colors.pressedColor=new Color(.5f,.75f,.9f); button.colors=colors;
        foreach(var frame in tab.GetComponentsInChildren<Image>(true).Where(i=>i.transform!=tab)) { frame.gameObject.SetActive(false); }
        var label=tab.GetComponentInChildren<TextMeshProUGUI>(true); label.gameObject.SetActive(true); Rect(label.transform,13,0,160,48); StyleText(label,30,selected?Cyan:Muted); label.text=title; label.alignment=TextAlignmentOptions.MidlineLeft;
        var index=Text(tab,"CategoryNumber",number,16,Muted,-98,0,30,34); index.gameObject.SetActive(true);
        // SelectedFrame is an existing hook used by SettingsPanel.SetTabSelected.
        var frameT=tab.Find("SelectedFrame");
        if(frameT!=null) { frameT.gameObject.SetActive(selected); Rect(frameT,-132,0,3,48); var frame=frameT.GetComponent<Image>(); frame.sprite=null; frame.color=Cyan; frame.raycastTarget=false; }
    }
    static void StylePage(Transform page,string heading,string description,string[] groups,string[] labels,bool[] enabled,bool audio)
    {
        Rect(page,182,-18,1092,606); DisableLayout(page);
        Text(page,"PageHeading",heading,30,White,-242,241,520,45);
        Text(page,"PageDescription",description,18,Muted,-242,200,520,30);
        int row=0;
        for(int i=0;i<groups.Length;i++) {
            var group=Need(page,groups[i]); DisableLayout(group);
            float y=113-row*124;
            Rect(group,0,y,966,106); group.gameObject.SetActive(enabled[i]);
            StyleSliderGroup(group,labels[i],audio);
            if(enabled[i]) row++;
        }
        page.gameObject.SetActive(audio);
    }
    static void StyleSliderGroup(Transform group,string label,bool audio)
    {
        var row=Image(group,"SettingsRowFrame",Sprite("choice.png"),new Color(.78f,.9f,1,.8f),0,0,974,100); row.transform.SetAsFirstSibling();
        var texts=group.GetComponentsInChildren<TextMeshProUGUI>(true);
        var caption=texts.FirstOrDefault(t=>t.name=="Label");
        var value=texts.FirstOrDefault(t=>t.name=="ValueText");
        if(caption==null||value==null) throw new Exception("Slider labels missing: "+group.name);
        caption.text=label; Rect(caption.transform,-288,10,266,40); StyleText(caption,27,White); caption.alignment=TextAlignmentOptions.MidlineLeft;
        Rect(value.transform,354,10,188,40); StyleText(value,27,Cyan); value.alignment=TextAlignmentOptions.MidlineRight;
        if(audio) value.text="100%";
        var slider=group.GetComponentInChildren<Slider>(true); if(slider==null||slider.fillRect==null||slider.handleRect==null) throw new Exception("Slider references missing: "+group.name);
        Rect(slider.transform,65,-23,608,44);
        var hitArea=slider.GetComponent<Image>(); if(hitArea==null) hitArea=slider.gameObject.AddComponent<Image>(); hitArea.sprite=null; hitArea.color=Color.clear; hitArea.raycastTarget=true;
        var background=slider.transform.Find("Background"); if(background!=null) { Rect(background,0,0,608,6); var b=background.GetComponent<Image>(); b.sprite=null; b.color=Track; b.raycastTarget=true; }
        var fillArea=slider.fillRect.parent as RectTransform; Stretch(fillArea,0,19,0,19);
        var fillImage=slider.fillRect.GetComponent<Image>(); fillImage.sprite=null; fillImage.color=Fill; fillImage.raycastTarget=false; slider.fillRect.offsetMin=Vector2.zero; slider.fillRect.offsetMax=Vector2.zero;
        var handleArea=slider.handleRect.parent as RectTransform; Stretch(handleArea,0,0,0,0); handleArea.anchorMin=new Vector2(0,.5f); handleArea.anchorMax=new Vector2(1,.5f); handleArea.sizeDelta=new Vector2(0,24); handleArea.anchoredPosition=Vector2.zero;
        slider.handleRect.anchorMin=new Vector2(slider.handleRect.anchorMin.x,0); slider.handleRect.anchorMax=new Vector2(slider.handleRect.anchorMax.x,1);
        slider.handleRect.sizeDelta=new Vector2(8,0); slider.handleRect.anchoredPosition=Vector2.zero;
        var handle=slider.handleRect.GetComponent<Image>(); handle.sprite=null; handle.color=Cyan; handle.raycastTarget=true; slider.targetGraphic=handle;
        if(audio) { slider.SetValueWithoutNotify(slider.maxValue); value.text="100%"; }
        else value.text=group.name=="TextSpeedGroup" ? $"{slider.value:F2} 秒/字" : $"{slider.value:F1} 秒";
        var colors=slider.colors; colors.normalColor=Color.white; colors.highlightedColor=Color.white; colors.pressedColor=new Color(.7f,.9f,1); colors.selectedColor=Color.white; slider.colors=colors;
    }
    static void StyleDisplayPage(Transform page,SettingsPanel settings)
    {
        Rect(page,182,-18,1092,606); DisableLayout(page);
        Text(page,"PageHeading","显示设置",30,White,-242,241,520,45);
        Text(page,"PageDescription","选择显示模式与画面分辨率",18,Muted,-242,200,520,30);
        string[] names={"DisplayModeGroup","ResolutionGroup"}; string[] labels={"显示模式","分辨率"}; bool[] enabled={settings.enableDisplayMode,settings.enableResolution};
        for(int i=0;i<2;i++) {
            var group=Need(page,names[i]); DisableLayout(group); Rect(group,0,113-i*124,966,106); group.gameObject.SetActive(enabled[i]);
            var row=Image(group,"SettingsRowFrame",Sprite("choice.png"),new Color(.78f,.9f,1,.8f),0,0,974,100); row.transform.SetAsFirstSibling();
            var dropdown=group.GetComponentInChildren<TMP_Dropdown>(true);
            if(dropdown==null) throw new Exception("Dropdown missing: "+names[i]);
            var label=group.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t=>!t.transform.IsChildOf(dropdown.transform));
            if(label!=null) { label.text=labels[i]; Rect(label.transform,-290,0,260,40); StyleText(label,27,White); }
            Rect(dropdown.transform,188,0,420,56);
            foreach(var t in dropdown.GetComponentsInChildren<TextMeshProUGUI>(true)) StyleText(t,24,White);
            var image=dropdown.GetComponent<Image>(); if(image!=null) { image.sprite=Sprite("namebox.png"); image.type=UnityEngine.UI.Image.Type.Simple; image.color=Color.white; }
            var arrow=dropdown.transform.Find("Arrow"); if(arrow!=null) { var a=arrow.GetComponent<Image>(); if(a!=null) { a.sprite=Sprite("图层 2.png"); a.color=Cyan; } Rect(arrow,177,0,16,12); }
        }
        if(!enabled.Any(v=>v)) Text(page,"UnavailableHint","当前平台使用默认显示设置",24,Muted,0,-24,800,60).alignment=TextAlignmentOptions.Center;
        page.gameObject.SetActive(false);
    }
    static void StyleClose(Transform close)
    {
        Rect(close,706,341,64,68); var button=close.GetComponent<Button>(); var image=close.GetComponent<Image>(); image.sprite=Sprite("close.png"); image.type=UnityEngine.UI.Image.Type.Simple; image.color=Color.white; image.preserveAspect=true; image.raycastTarget=true; button.targetGraphic=image;
        foreach(var text in close.GetComponentsInChildren<TextMeshProUGUI>(true)) text.gameObject.SetActive(false);
    }
    static void Validate(GameObject root)
    {
        if(root==null) throw new Exception("Saved prefab not loaded.");
        foreach(var t in root.GetComponentsInChildren<Transform>(true)) if(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject)!=0) throw new Exception("Missing script: "+t.name);
        foreach(string name in new[]{"VolumePageBtn","TextPageBtn","DisplayPageBtn","CloseBtn"}) if(Need(root.transform,name).GetComponent<Button>()==null) throw new Exception("Button missing: "+name);
        foreach(string name in new[]{"MasterVolumeGroup","BGMVolumeGroup","VoiceVolumeGroup","SFXVolumeGroup","TextSpeedGroup","AutoSpeedGroup"}) {
            var group=Need(root.transform,name); var slider=group.GetComponentInChildren<Slider>(true);
            if(slider==null||slider.fillRect==null||slider.handleRect==null||slider.targetGraphic==null) throw new Exception("Broken slider: "+name);
            if(Find(group,"ValueText")==null) throw new Exception("Value label missing: "+name);
        }
        foreach(string name in new[]{"DisplayModeGroup","ResolutionGroup"}) { var d=Need(root.transform,name).GetComponentInChildren<TMP_Dropdown>(true); if(d==null||d.template==null||d.captionText==null) throw new Exception("Broken dropdown: "+name); }
        foreach(var text in root.GetComponentsInChildren<TextMeshProUGUI>(true)) if(text.font==null) throw new Exception("Missing font: "+text.name);
        foreach(var image in root.GetComponentsInChildren<Image>(true)) if(image.name.StartsWith("SettingsRowFrame")&&image.sprite==null) throw new Exception("Missing new art reference.");
    }
    static IEnumerator RenderPreviews()
    {
        var root=PrefabUtility.LoadPrefabContents(Target);
        try {
            var scene=root.scene; EditorSceneManager.SetSceneCullingMask(scene,1UL<<63);
            var cameraObject=new GameObject("SettingsPreviewCamera",typeof(Camera)); cameraObject.layer=5; SceneManager.MoveGameObjectToScene(cameraObject,scene);
            var camera=cameraObject.GetComponent<Camera>(); camera.scene=scene; camera.overrideSceneCullingMask=EditorSceneManager.GetSceneCullingMask(scene); camera.transform.position=new Vector3(0,0,-100); camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(.015f,.025f,.04f,1); camera.orthographic=true; camera.orthographicSize=540; camera.nearClipPlane=.1f; camera.farClipPlane=1000; camera.cullingMask=1<<5;
            var canvasObject=new GameObject("SettingsPreviewCanvas",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler)); canvasObject.layer=5; SceneManager.MoveGameObjectToScene(canvasObject,scene);
            var canvas=canvasObject.GetComponent<Canvas>(); canvas.renderMode=RenderMode.WorldSpace; canvas.worldCamera=camera; var canvasRect=(RectTransform)canvasObject.transform; canvasRect.sizeDelta=new Vector2(1920,1080); canvasRect.position=Vector3.zero; canvasRect.localScale=Vector3.one;
            var scaler=canvasObject.GetComponent<CanvasScaler>(); scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution=new Vector2(1920,1080); scaler.matchWidthOrHeight=.5f;
            root.transform.SetParent(canvasObject.transform,false); Rect(root.transform,0,0,1600,820);
            var rootCanvas=root.GetComponent<Canvas>(); rootCanvas.overrideSorting=false;
            var target=new RenderTexture(1920,1080,24); target.Create(); camera.targetTexture=target;
            EditorApplication.QueuePlayerLoopUpdate(); yield return null; yield return null;
            string[] pages={"VolumePage","TextPage","DisplayPage"};
            for(int page=0;page<pages.Length;page++) {
                for(int i=0;i<pages.Length;i++) Need(root.transform,pages[i]).gameObject.SetActive(i==page);
                var panel=root.GetComponent<SettingsPanel>(); var method=typeof(SettingsPanel).GetMethod("SetTabSelected",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                string[] tabs={"VolumePageBtn","TextPageBtn","DisplayPageBtn"};
                for(int i=0;i<3;i++) method.Invoke(panel,new object[]{Need(root.transform,tabs[i]).GetComponent<Button>(),i==page});
                Canvas.ForceUpdateCanvases(); foreach(var text in root.GetComponentsInChildren<TextMeshProUGUI>(true)) if(text.gameObject.activeInHierarchy) text.ForceMeshUpdate(true,true);
                EditorApplication.QueuePlayerLoopUpdate(); yield return null; yield return null;
                var corners=new Vector3[4]; ((RectTransform)root.transform).GetWorldCorners(corners);
                File.WriteAllText(Path.Combine(Output,"render-diagnostics.txt"),"scene="+scene.name+" loaded="+scene.isLoaded+" mask="+EditorSceneManager.GetSceneCullingMask(scene)+" camera="+camera.transform.position+" rootActive="+root.activeInHierarchy+" rootCanvas="+rootCanvas.enabled+" canvas="+canvas.enabled+" rect="+((RectTransform)root.transform).rect+" corners="+string.Join(";",corners.Select(c=>c.ToString()))+" graphics="+root.GetComponentsInChildren<Graphic>().Length);
                camera.Render();
                var previous=RenderTexture.active; RenderTexture.active=target; var png=new Texture2D(1920,1080,TextureFormat.RGB24,false); png.ReadPixels(new Rect(0,0,1920,1080),0,0); png.Apply(); RenderTexture.active=previous;
                if(png.GetPixels32().Where((pixel,index)=>index%103==0).Distinct().Count()<16) throw new Exception("Preview is blank: "+pages[page]);
                File.WriteAllBytes(Path.Combine(Output,pages[page]+".png"),png.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(png);
            }
            camera.targetTexture=null; target.Release(); UnityEngine.Object.DestroyImmediate(target);
        } finally { PrefabUtility.UnloadPrefabContents(root); }
    }
}
#endif