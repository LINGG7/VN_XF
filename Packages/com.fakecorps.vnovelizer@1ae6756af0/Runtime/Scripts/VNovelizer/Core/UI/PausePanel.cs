using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Serialization;
using UnityEngine.SceneManagement;
using PrimeTween;
using VNovelizer.Core.API;

/// <summary>
/// 暂停面板
/// </summary>
public class PausePanel : BasePanel
{
    #region UI控件引用

    [SerializeField] private Button saveBtn;
    [SerializeField] private Button loadBtn;
    [SerializeField] private Button settingsBtn;
    [SerializeField] private Button exitBtn;
    [SerializeField] private Button closeBtn;

    [Header("Square Glitch Dissolve")]
    [FormerlySerializedAs("enableSlideAnimation")]
    [SerializeField] private bool enableDissolveAnimation = true;
    [FormerlySerializedAs("slideDuration")]
    [SerializeField, Min(0.01f)] private float dissolveDuration = 0.5f;
    [SerializeField, Min(8f)] private float dissolveBlockSize = 30f;
    [SerializeField] private Color dissolveEdgeColor = new Color(0.15f, 0.9f, 1f, 0.85f);

    private RectTransform panelRect;
    private CanvasGroup panelCanvasGroup;
    private Coroutine dissolveAnimationCoroutine;
    private bool isDissolvingOut;
    private bool restoreStateAfterDissolve;
    private System.Action dissolveOutCompleted;
    private float dissolveProgress = 1f;
    private Mask dissolveMask;
    private PausePanelSquareDissolveGraphic dissolveGraphic;
    private PausePanelSquareDissolveGraphic dissolveEdges;
    private GameObject inputBlocker;
    private const int InputBlockerSortingOrder = 6;

    #endregion

    #region 初始化

    protected override void Awake()
    {
        base.Awake();
        InitializeDissolveAnimation();
        
        // 初始化控件
        InitializeControls();
        
        // 绑定事件
        BindEvents();
    }

    private void InitializeDissolveAnimation()
    {
        panelRect = transform as RectTransform;
        panelCanvasGroup = GetComponent<CanvasGroup>();
        if (panelCanvasGroup == null)
        {
            panelCanvasGroup = gameObject.AddComponent<CanvasGroup>();
        }
    }
    /// <summary>
    /// 初始化控件
    /// </summary>
    private void InitializeControls()
    {
        saveBtn = GetControl<Button>("SaveBtn");
        loadBtn = GetControl<Button>("LoadBtn");
        settingsBtn = GetControl<Button>("SettingsBtn");
        exitBtn = GetControl<Button>("ExitBtn");
        closeBtn = GetControl<Button>("CloseBtn");
        
        // 检查关键控件是否存在
        if (saveBtn == null)
            Debug.LogError("[PausePanel] 找不到 SaveBtn 按钮！");
        if (loadBtn == null)
            Debug.LogError("[PausePanel] 找不到 LoadBtn 按钮！");
        if (settingsBtn == null)
            Debug.LogError("[PausePanel] 找不到 SettingsBtn 按钮！");
        if (exitBtn == null)
            Debug.LogError("[PausePanel] 找不到 ExitBtn 按钮！");
        if (closeBtn == null)
            Debug.LogError("[PausePanel] 找不到 CloseBtn 按钮！");
    }
    
    /// <summary>
    /// 绑定事件
    /// </summary>
    private void BindEvents()
    {
        if (saveBtn != null)
            saveBtn.onClick.AddListener(OnSaveBtnClick);
        if (loadBtn != null)
            loadBtn.onClick.AddListener(OnLoadBtnClick);
        if (settingsBtn != null)
            settingsBtn.onClick.AddListener(OnSettingsBtnClick);
        if (exitBtn != null)
            exitBtn.onClick.AddListener(OnExitBtnClick);
        if (closeBtn != null)
            closeBtn.onClick.AddListener(OnCloseBtnClick);
    }
    
    /// <summary>
    /// 解绑事件（用于清理）
    /// </summary>
    private void UnbindEvents()
    {
        if (saveBtn != null)
            saveBtn.onClick.RemoveListener(OnSaveBtnClick);
        if (loadBtn != null)
            loadBtn.onClick.RemoveListener(OnLoadBtnClick);
        if (settingsBtn != null)
            settingsBtn.onClick.RemoveListener(OnSettingsBtnClick);
        if (exitBtn != null)
            exitBtn.onClick.RemoveListener(OnExitBtnClick);
        if (closeBtn != null)
            closeBtn.onClick.RemoveListener(OnCloseBtnClick);
    }
    
    #endregion
    
    #region Unity生命周期
    
    protected override void OnEnable()
    {
        base.OnEnable();
        
        // 每次打开面板时设置状态
        // 检查是否可以打开（允许在Gameplay、AutoPlay、Choice和Pause状态下打开）
        GameState currentState = GameStateManager.GetInstance().CurrentState;
        if (currentState != GameState.Gameplay && currentState != GameState.AutoPlay && 
            currentState != GameState.Choice && currentState != GameState.Pause)
        {
            Debug.LogWarning($"[PausePanel] 当前状态 {currentState} 不允许打开暂停面板，已关闭");
            gameObject.SetActive(false);
            return;
        }
        
        // 只有在非Pause状态时才设置Pause状态
        // 如果已经在Pause状态，不需要重复设置
        if (currentState != GameState.Pause)
        {
            GameStateManager.GetInstance().SetState(GameState.Pause);
        }
    }
    
    public override void ShowMe()
    {
        gameObject.SetActive(true);
        if (!gameObject.activeInHierarchy)
        {
            return;
        }

        // Reopening cancels any pending close callback, including deferred destruction.
        isDissolvingOut = false;
        dissolveOutCompleted = null;
        restoreStateAfterDissolve = false;
        EnsureInputBlocker();
        PlayDissolveAnimation();
    }
    
    public override void HideMe()
    {
        HideWithDissolve();
    }

    public void HideWithDissolve(System.Action onHidden = null, bool restoreGameState = true)
    {
        // Repeated clicks may register cleanup but must not restart the animation or change navigation intent.
        if (isDissolvingOut)
        {
            dissolveOutCompleted += onHidden;
            return;
        }

        isDissolvingOut = true;
        restoreStateAfterDissolve = restoreGameState;
        dissolveOutCompleted = onHidden;
        StopDissolveAnimation();
        if (!isActiveAndEnabled || !enableDissolveAnimation || panelRect == null || dissolveProgress <= 0f)
        {
            CompleteDissolveHide();
            return;
        }

        PrepareDissolveGraphics();
        dissolveAnimationCoroutine = StartCoroutine(DissolveAnimationCoroutine(0f));
    }

    private void CompleteDissolveHide()
    {
        System.Action onHidden = dissolveOutCompleted;
        bool restoreGameState = restoreStateAfterDissolve;
        dissolveOutCompleted = null;
        isDissolvingOut = false;
        dissolveAnimationCoroutine = null;
        // Deactivate before removing the mask, avoiding a full-panel flash on the last frame.
        HideInputBlocker();
        gameObject.SetActive(false);
        if (restoreGameState && GameStateManager.GetInstance() != null &&
            GameStateManager.GetInstance().CurrentState == GameState.Pause)
        {
            GameStateManager.GetInstance().RestoreState();
        }
        onHidden?.Invoke();
    }
    private void OnDestroy()
    {
        StopDissolveAnimation();
        ResetDissolveAnimationState();
        HideInputBlocker();

        // 清理事件监听
        UnbindEvents();
        
        // 面板被Destroy时，如果当前状态是Pause，需要恢复游戏状态
        if (GameStateManager.GetInstance() != null && 
            GameStateManager.GetInstance().CurrentState == GameState.Pause)
        {
            GameStateManager.GetInstance().RestoreState();
            Debug.Log("[PausePanel] 面板被Destroy，已恢复游戏状态");
        }
    }
    
    #endregion

    #region 入场动画

    private void PlayDissolveAnimation()
    {
        StopDissolveAnimation();
        ResetDissolveAnimationState();
        dissolveProgress = 0f;
        if (!enableDissolveAnimation || panelRect == null)
        {
            dissolveProgress = 1f;
            return;
        }

        PrepareDissolveGraphics();
        dissolveAnimationCoroutine = StartCoroutine(DissolveAnimationCoroutine(1f));
    }

    private void PrepareDissolveGraphics()
    {
        EnsureDissolveGraphics();
        // One stencil mask reveals both the UI images and TMP text without changing their materials.
        // Include protruding controls and keep the panel in place during both entry and exit.
        Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(panelRect);
        Rect revealRect = Rect.MinMaxRect(bounds.min.x, bounds.min.y, bounds.max.x, bounds.max.y);
        float blockSize = Mathf.Max(8f, dissolveBlockSize,
            Mathf.Max(revealRect.width, revealRect.height) / 48f);
        dissolveGraphic.Configure(revealRect, blockSize, false, Color.white);
        dissolveEdges.Configure(revealRect, blockSize, true, dissolveEdgeColor);
        SetDissolveProgress(dissolveProgress, 0f);
        dissolveGraphic.enabled = true;
        dissolveMask.enabled = true;
        dissolveEdges.enabled = true;
        panelCanvasGroup.alpha = 1f;
        panelCanvasGroup.interactable = false;
        panelCanvasGroup.blocksRaycasts = true;
    }
    private void EnsureDissolveGraphics()
    {
        if (dissolveGraphic == null)
        {
            dissolveGraphic = gameObject.AddComponent<PausePanelSquareDissolveGraphic>();
            dissolveGraphic.raycastTarget = false;
            dissolveGraphic.enabled = false;
            dissolveMask = gameObject.AddComponent<Mask>();
            dissolveMask.showMaskGraphic = false;
            dissolveMask.enabled = false;
        }

        if (dissolveEdges == null)
        {
            GameObject edgeObject = new GameObject("PauseDissolveEdges",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(PausePanelSquareDissolveGraphic));
            edgeObject.layer = gameObject.layer;
            RectTransform edgeRect = (RectTransform)edgeObject.transform;
            edgeRect.SetParent(transform, false);
            edgeRect.anchorMin = Vector2.zero;
            edgeRect.anchorMax = Vector2.one;
            edgeRect.offsetMin = Vector2.zero;
            edgeRect.offsetMax = Vector2.zero;
            edgeRect.pivot = panelRect.pivot;
            dissolveEdges = edgeObject.GetComponent<PausePanelSquareDissolveGraphic>();
            dissolveEdges.raycastTarget = false;
            dissolveEdges.enabled = false;
        }

        dissolveEdges.transform.SetAsLastSibling();
    }

    private IEnumerator DissolveAnimationCoroutine(float targetProgress)
    {
        float startProgress = dissolveProgress;
        float duration = Mathf.Max(0.01f, dissolveDuration * Mathf.Abs(targetProgress - startProgress));
        float elapsed = 0f;
        while (elapsed < duration)
        {
            float progress = Mathf.Lerp(startProgress, targetProgress, Mathf.Clamp01(elapsed / duration));
            SetDissolveProgress(progress, elapsed);
            yield return null;
            elapsed += Time.unscaledDeltaTime;
        }

        dissolveAnimationCoroutine = null;
        SetDissolveProgress(targetProgress, elapsed);
        if (isDissolvingOut)
        {
            CompleteDissolveHide();
        }
        else
        {
            ResetDissolveAnimationState();
        }
    }

    private void SetDissolveProgress(float value, float elapsed)
    {
        dissolveProgress = Mathf.Clamp01(value);
        int flickerFrame = Mathf.FloorToInt(elapsed * 28f);
        dissolveGraphic.SetProgress(dissolveProgress, flickerFrame);
        dissolveEdges.SetProgress(dissolveProgress, flickerFrame);
    }
    private void StopDissolveAnimation()
    {
        if (dissolveAnimationCoroutine != null)
        {
            StopCoroutine(dissolveAnimationCoroutine);
            dissolveAnimationCoroutine = null;
        }
    }

    private void ResetDissolveAnimationState()
    {
        // Disable the mask before its Graphic so stencil state is restored on every child.
        if (dissolveMask != null) dissolveMask.enabled = false;
        if (dissolveGraphic != null) dissolveGraphic.enabled = false;
        if (dissolveEdges != null) dissolveEdges.enabled = false;
        if (panelCanvasGroup != null)
        {
            panelCanvasGroup.alpha = 1f;
            panelCanvasGroup.interactable = true;
            panelCanvasGroup.blocksRaycasts = true;
        }
    }

    private void OnDisable()
    {
        StopDissolveAnimation();
        ResetDissolveAnimationState();
        HideInputBlocker();
        isDissolvingOut = false;
        dissolveOutCompleted = null;
        restoreStateAfterDissolve = false;
        dissolveProgress = 1f;
    }

    #endregion
    #region 输入遮罩

    private void EnsureInputBlocker()
    {
        const string blockerName = "PauseInputBlocker";
        Transform parent = transform.parent;
        if (parent == null)
        {
            return;
        }

        if (inputBlocker == null)
        {
            Transform existingBlocker = parent.Find(blockerName);
            inputBlocker = existingBlocker != null
                ? existingBlocker.gameObject
                : new GameObject(blockerName, typeof(RectTransform), typeof(Image), typeof(Button));
        }

        inputBlocker.transform.SetParent(parent, false);
        inputBlocker.transform.SetAsLastSibling();
        transform.SetAsLastSibling();
        inputBlocker.SetActive(true);

        Canvas blockerCanvas = inputBlocker.GetComponent<Canvas>();
        if (blockerCanvas == null)
        {
            blockerCanvas = inputBlocker.AddComponent<Canvas>();
        }
        blockerCanvas.overrideSorting = true;
        blockerCanvas.sortingOrder = InputBlockerSortingOrder;

        GraphicRaycaster blockerRaycaster = inputBlocker.GetComponent<GraphicRaycaster>();
        if (blockerRaycaster == null)
        {
            blockerRaycaster = inputBlocker.AddComponent<GraphicRaycaster>();
        }

        Canvas panelCanvas = GetComponent<Canvas>();
        if (panelCanvas == null)
        {
            panelCanvas = gameObject.AddComponent<Canvas>();
        }
        panelCanvas.overrideSorting = true;
        panelCanvas.sortingOrder = InputBlockerSortingOrder + 1;

        GraphicRaycaster panelRaycaster = GetComponent<GraphicRaycaster>();
        if (panelRaycaster == null)
        {
            panelRaycaster = gameObject.AddComponent<GraphicRaycaster>();
        }

        RectTransform blockerRect = inputBlocker.transform as RectTransform;
        blockerRect.anchorMin = Vector2.zero;
        blockerRect.anchorMax = Vector2.one;
        blockerRect.offsetMin = Vector2.zero;
        blockerRect.offsetMax = Vector2.zero;

        Image blockerImage = inputBlocker.GetComponent<Image>();
        if (blockerImage == null)
        {
            blockerImage = inputBlocker.AddComponent<Image>();
        }
        blockerImage.color = new Color(0f, 0f, 0f, 0f);
        blockerImage.raycastTarget = true;

        Button blockerButton = inputBlocker.GetComponent<Button>();
        if (blockerButton == null)
        {
            blockerButton = inputBlocker.AddComponent<Button>();
        }
        blockerButton.transition = Selectable.Transition.None;
        blockerButton.onClick.RemoveListener(OnCloseBtnClick);
        blockerButton.onClick.AddListener(OnCloseBtnClick);
    }

    private void HideInputBlocker()
    {
        if (inputBlocker != null)
        {
            inputBlocker.SetActive(false);
        }
    }

    #endregion
    
    #region 按钮事件处理
    
    /// <summary>
    /// 保存按钮点击
    /// </summary>
    private void OnSaveBtnClick()
    {
        // 检查是否可以打开保存系统面板
        if (!GameStateManager.GetInstance().CanOpenPanel(GameState.SaveLoad))
        {
            Debug.LogWarning("[PausePanel] 当前状态不允许打开保存系统面板");
            return;
        }
        
        // 【Bug修复】截图已经在打开PausePanel之前完成，这里不需要再次截屏
        OpenSaveLoadPanelWithDarkFade(SaveLoadPanel.Mode.Save);
    }
    
    /// <summary>
    /// 加载按钮点击
    /// </summary>
    private void OnLoadBtnClick()
    {
        // 检查是否可以打开保存系统面板
        if (!GameStateManager.GetInstance().CanOpenPanel(GameState.SaveLoad))
        {
            Debug.LogWarning("[PausePanel] 当前状态不允许打开保存系统面板");
            return;
        }
        
        OpenSaveLoadPanelWithDarkFade(SaveLoadPanel.Mode.Load);
    }

    private void OpenSaveLoadPanelWithDarkFade(SaveLoadPanel.Mode mode)
    {
        if (isDissolvingOut) return;
        HideWithDissolve(() =>
        {
            // Keep Pause until the save/load panel takes over, then use the existing dark transition.
            UIManager.GetInstance().ShowPanelWithDarkFade<SaveLoadPanel>(
                "SaveLoadPanel",
                VNProjectConfig.Instance.UI_SaveLoadPath,
                E_UI_Layer.Top,
                panel =>
                {
                    if (panel == null) return;
                    panel.SetMode(mode);
                });
        }, restoreGameState: false);
    }
    /// <summary>
    /// 设置按钮点击
    /// </summary>
    private void OnSettingsBtnClick()
    {
        if (isDissolvingOut) return;
        if (!GameStateManager.GetInstance().CanOpenPanel(GameState.Settings))
        {
            Debug.LogWarning("[PausePanel] 当前状态不允许打开设置面板");
            return;
        }

        HideWithDissolve(() =>
        {
            string path = VNProjectConfig.Instance != null ? VNProjectConfig.Instance.UI_SettingsPath : "VNPrefabs/UI/Settings";
            if (string.IsNullOrEmpty(path)) path = "VNPrefabs/UI/Settings";
            UIManager.GetInstance().ShowPanel<SettingsPanel>("SettingsPanel", path, E_UI_Layer.Top, null);
        }, restoreGameState: false);
    }
    /// <summary>
    /// 退出按钮点击 - 返回主菜单场景
    /// </summary>
    private void OnExitBtnClick()
    {
        if (isDissolvingOut) return;
        VNManager.GetInstance().AutoSaveGame();
        HideWithDissolve(() =>
        {
            PrimeTween.Tween.StopAll();
            VNAPI.ClearAllEffects();
            PoolManager.GetInstance().Clear();
            UIManager.GetInstance().HidePanel("PausePanel");
            SceneManager.LoadScene("VNMainMenu");
            Debug.Log("[PausePanel] 返回主菜单场景");
        });
    }
    /// <summary>
    /// 关闭按钮点击
    /// </summary>
    private void OnCloseBtnClick()
    {
        HideMe();
    }

        #endregion
    }


/// <summary>
/// Runtime-only square stencil/edge mesh for PausePanel. No textures or saved materials are needed.
/// </summary>
[AddComponentMenu("")]
public sealed class PausePanelSquareDissolveGraphic : MaskableGraphic
{
    private Rect revealRect;
    private float blockSize = 30f;
    private float progress;
    private int flickerFrame;
    private bool edgesOnly;
    private Color edgeColor;

    public void Configure(Rect bounds, float size, bool drawEdges, Color tint)
    {
        revealRect = bounds;
        blockSize = Mathf.Max(8f, size);
        edgesOnly = drawEdges;
        edgeColor = tint;
        color = Color.white;
        raycastTarget = false;
        SetVerticesDirty();
    }

    public void SetProgress(float value, int frame)
    {
        progress = Mathf.Clamp01(value);
        flickerFrame = frame;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        if (progress <= 0f || revealRect.width <= 0f || revealRect.height <= 0f)
        {
            return;
        }

        int columns = Mathf.CeilToInt(revealRect.width / blockSize);
        int rows = Mathf.CeilToInt(revealRect.height / blockSize);
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                // Stable tile thresholds make coverage grow from sparse fragments to the full panel.
                float threshold = Hash(x, y) * 0.7f + Hash(x / 3 + 191, y / 3 + 71) * 0.3f;
                float phase = progress >= 1f ? 1f : Mathf.Clamp01((progress - threshold * 0.72f) / 0.28f);
                float squareScale = phase >= 1f ? 1f : Mathf.Floor(phase * 5f) / 5f;
                if (squareScale <= 0f) continue;

                Vector2 center = new Vector2(revealRect.xMin + (x + 0.5f) * blockSize,
                    revealRect.yMin + (y + 0.5f) * blockSize);
                float halfSize = blockSize * squareScale * 0.5f;
                Rect tile = Rect.MinMaxRect(Mathf.Max(revealRect.xMin, center.x - halfSize),
                    Mathf.Max(revealRect.yMin, center.y - halfSize),
                    Mathf.Min(revealRect.xMax, center.x + halfSize),
                    Mathf.Min(revealRect.yMax, center.y + halfSize));
                if (tile.width <= 0f || tile.height <= 0f) continue;

                if (!edgesOnly)
                {
                    AddQuad(mesh, tile, Color.white);
                    continue;
                }

                if (phase >= 1f) continue;
                float flicker = Hash(x + flickerFrame * 7, y + flickerFrame * 13);
                Color tint = flicker > 0.84f
                    ? new Color(0.85f, 0.25f, 1f, edgeColor.a)
                    : edgeColor;
                tint.a *= (1f - phase) * Mathf.Lerp(0.5f, 1f, flicker);
                float line = Mathf.Min(1.8f, Mathf.Min(tile.width, tile.height) * 0.2f);
                AddQuad(mesh, new Rect(tile.xMin, tile.yMin, tile.width, line), tint);
                AddQuad(mesh, new Rect(tile.xMin, tile.yMax - line, tile.width, line), tint);
                AddQuad(mesh, new Rect(tile.xMin, tile.yMin + line, line, tile.height - 2f * line), tint);
                AddQuad(mesh, new Rect(tile.xMax - line, tile.yMin + line, line, tile.height - 2f * line), tint);
            }
        }
    }

    private static float Hash(int x, int y)
    {
        unchecked
        {
            uint value = (uint)x * 374761393u + (uint)y * 668265263u + 2246822519u;
            value = (value ^ (value >> 13)) * 1274126177u;
            value ^= value >> 16;
            return (value & 0x00ffffffu) / 16777215f;
        }
    }

    private static void AddQuad(VertexHelper mesh, Rect rect, Color tint)
    {
        if (rect.width <= 0f || rect.height <= 0f) return;
        int start = mesh.currentVertCount;
        UIVertex vertex = UIVertex.simpleVert;
        vertex.color = tint;
        vertex.uv0 = Vector2.zero;
        vertex.position = new Vector3(rect.xMin, rect.yMin);
        mesh.AddVert(vertex);
        vertex.position = new Vector3(rect.xMin, rect.yMax);
        mesh.AddVert(vertex);
        vertex.position = new Vector3(rect.xMax, rect.yMax);
        mesh.AddVert(vertex);
        vertex.position = new Vector3(rect.xMax, rect.yMin);
        mesh.AddVert(vertex);
        mesh.AddTriangle(start, start + 1, start + 2);
        mesh.AddTriangle(start, start + 2, start + 3);
    }
}