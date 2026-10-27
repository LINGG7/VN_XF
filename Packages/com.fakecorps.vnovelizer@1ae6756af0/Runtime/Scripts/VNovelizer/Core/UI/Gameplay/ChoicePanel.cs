using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class ChoicePanel : BasePanel
{
    [Header("Choice Fade In")]
    [SerializeField, Min(0f)] private float choiceFadeDuration = 0.3f;

    private Transform container;
    private GameObject choiceItemPrefab;
    private List<GameObject> activeItems = new List<GameObject>();
    private CanvasGroup canvasGroup;
    private bool isGameplayUIHidden;
    private bool isGameplayUIInputBlocked;
    private readonly List<ChoiceFade> fadingChoices = new List<ChoiceFade>();
    private Coroutine choiceFadeCoroutine;

    private sealed class ChoiceFade
    {
        public CanvasGroup Group;
        public float TargetAlpha;
        public bool Interactable;
        public bool BlocksRaycasts;
        public float Elapsed;
    }

    protected override void Awake()
    {
        base.Awake();
        container = transform.Find("ChoiceContainer");
        canvasGroup = GetComponent<CanvasGroup>();
        if (canvasGroup == null)
            canvasGroup = gameObject.AddComponent<CanvasGroup>();
        // 记得把路径配置到 VNProjectConfig 或者硬编码
        choiceItemPrefab = ResourcesManager.GetInstance().Load<GameObject>(VNProjectConfig.Instance.UI_ChoicePath + "/ChoiceItem");
    }

    /// <summary>
    /// 显示选项
    /// </summary>
    /// <param name="choices">选项数据列表 (Text, CommandString)</param>
    public void ShowChoices(List<ChoiceData> choices)
    {
        EnsurePanelVisible();

        // 清理旧按钮
        StopChoiceFades();
        foreach (var item in activeItems) Destroy(item);
        activeItems.Clear();

        // 生成新按钮
        foreach (var data in choices)
        {
            GameObject btnObj = Instantiate(choiceItemPrefab, container);
            activeItems.Add(btnObj);

            SetupChoiceItem(btnObj, data.Text, data.Command, data.SelectedFlagKey);
            BeginChoiceFade(btnObj);
        }

        ShowMe();
    }

    private void OnChoiceClicked(string command, string selectedFlagKey)
    {
        if (isGameplayUIHidden || isGameplayUIInputBlocked ||
            GameStateManager.GetInstance().CurrentState != GameState.Choice)
        {
            return;
        }

        if (!string.IsNullOrEmpty(selectedFlagKey))
        {
            GlobalDataManager.GetInstance().SetBoolFlag(selectedFlagKey, true);
        }

        // 关闭面板
        UIManager.GetInstance().HidePanel("ChoicePanel");

        GameStateManager.GetInstance().SetState(GameState.Gameplay);

        // 注意：这里需要调用 VNManager 或 CommandManager 来执行
        if (!string.IsNullOrEmpty(command))
        {

            VNManager.GetInstance().ExecuteChoiceCommand(command);
        }
        else
        {
            // 如果选项没配命令（比如只是“继续”），那就直接下一行
            VNManager.GetInstance().NextLine();
        }
    }

    // 在 ChoicePanel.cs 中添加/修改

    public void AddChoice(string text, string command)
    {
        AddChoice(text, command, "");
    }

    public void AddChoice(string text, string command, string selectedFlagKey)
    {
        EnsurePanelVisible();

        // 确保 Container 存在
        if (container == null) container = transform.Find("ChoiceContainer");
        if (choiceItemPrefab == null)
            choiceItemPrefab = ResourcesManager.GetInstance().Load<GameObject>(VNProjectConfig.Instance.UI_ChoicePath + "/ChoiceItem");

        if (choiceItemPrefab == null || container == null)
        {
            Debug.LogError("[ChoicePanel] Choice prefab or container is missing.");
            return;
        }

        GameObject btnObj = Instantiate(choiceItemPrefab, container);
        activeItems.Add(btnObj);
        btnObj.SetActive(true);

        SetupChoiceItem(btnObj, text, command, selectedFlagKey);
        BeginChoiceFade(btnObj);

        LayoutRebuilder.ForceRebuildLayoutImmediate(container as RectTransform);
        ShowMe();
    }

    private void SetupChoiceItem(GameObject btnObj, string text, string command, string selectedFlagKey)
    {
        TMP_Text textComp = btnObj.GetComponentInChildren<TMP_Text>();
        if (textComp != null) textComp.text = text;

        Transform selected = FindChildRecursive(btnObj.transform, "Selected");
        if (selected != null)
        {
            bool isSelected = !string.IsNullOrEmpty(selectedFlagKey) &&
                              GlobalDataManager.GetInstance().GetBoolFlag(selectedFlagKey);
            selected.gameObject.SetActive(isSelected);
        }

        Button btn = btnObj.GetComponent<Button>();
        if (btn == null)
        {
            Debug.LogWarning("[ChoicePanel] Choice item is missing a Button component.");
            return;
        }

        btn.onClick.RemoveAllListeners();
        btn.onClick.AddListener(() =>
        {
            if (btn.IsInteractable())
                OnChoiceClicked(command, selectedFlagKey);
        });
    }

    private void BeginChoiceFade(GameObject item)
    {
        if (choiceFadeDuration <= 0f) return;

        CanvasGroup group = item.GetComponent<CanvasGroup>();
        if (group == null) group = item.AddComponent<CanvasGroup>();
        fadingChoices.Add(new ChoiceFade
        {
            Group = group,
            TargetAlpha = group.alpha,
            Interactable = group.interactable,
            BlocksRaycasts = group.blocksRaycasts
        });
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = true;
        if (choiceFadeCoroutine == null)
            choiceFadeCoroutine = StartCoroutine(FadeChoicesIn());
    }

    private IEnumerator FadeChoicesIn()
    {
        while (fadingChoices.Count > 0)
        {
            // Leave newly created choices transparent for their first rendered frame.
            yield return null;
            for (int i = fadingChoices.Count - 1; i >= 0; i--)
            {
                ChoiceFade fade = fadingChoices[i];
                if (fade.Group == null)
                {
                    fadingChoices.RemoveAt(i);
                    continue;
                }

                fade.Elapsed += Time.unscaledDeltaTime;
                float t = choiceFadeDuration <= 0f ? 1f : Mathf.Clamp01(fade.Elapsed / choiceFadeDuration);
                fade.Group.alpha = Mathf.SmoothStep(0f, fade.TargetAlpha, t);
                if (t >= 1f)
                {
                    RestoreChoiceFade(fade);
                    fadingChoices.RemoveAt(i);
                }
            }
        }
        choiceFadeCoroutine = null;
    }

    private static void RestoreChoiceFade(ChoiceFade fade)
    {
        if (fade.Group == null) return;
        fade.Group.alpha = fade.TargetAlpha;
        fade.Group.interactable = fade.Interactable;
        fade.Group.blocksRaycasts = fade.BlocksRaycasts;
    }

    private void StopChoiceFades()
    {
        if (choiceFadeCoroutine != null)
        {
            StopCoroutine(choiceFadeCoroutine);
            choiceFadeCoroutine = null;
        }
        foreach (ChoiceFade fade in fadingChoices)
            RestoreChoiceFade(fade);
        fadingChoices.Clear();
    }

    private void OnDisable()
    {
        StopChoiceFades();
    }

    private Transform FindChildRecursive(Transform root, string childName)
    {
        if (root == null) return null;

        Transform directChild = root.Find(childName);
        if (directChild != null) return directChild;

        foreach (Transform child in root)
        {
            Transform found = FindChildRecursive(child, childName);
            if (found != null) return found;
        }

        return null;
    }

    public void SetGameplayUIHidden(bool hidden)
    {
        isGameplayUIHidden = hidden;
        ApplyGameplayUIState();
    }

    public void SetGameplayUIInputBlocked(bool blocked)
    {
        isGameplayUIInputBlocked = blocked;
        ApplyGameplayUIState();
    }

    private void ApplyGameplayUIState()
    {
        if (canvasGroup == null)
            return;

        bool canInteract = !isGameplayUIHidden && !isGameplayUIInputBlocked;
        canvasGroup.alpha = isGameplayUIHidden ? 0f : 1f;
        canvasGroup.interactable = canInteract;
        canvasGroup.blocksRaycasts = canInteract;
    }

    private void EnsurePanelVisible()
    {
        gameObject.SetActive(true);
        transform.SetAsLastSibling();

        if (canvasGroup == null)
            canvasGroup = GetComponent<CanvasGroup>();
        if (canvasGroup == null)
            canvasGroup = gameObject.AddComponent<CanvasGroup>();

        VNGameplayPanel gameplayPanel = UIManager.GetInstance().GetPanel<VNGameplayPanel>("VNGameplayPanel");
        isGameplayUIHidden = gameplayPanel != null && gameplayPanel.IsUIHidden;
        ApplyGameplayUIState();
    }

}

// 简单的数据结构
public class ChoiceData
{
    public string Text;
    public string Command;
    public string SelectedFlagKey;
}
