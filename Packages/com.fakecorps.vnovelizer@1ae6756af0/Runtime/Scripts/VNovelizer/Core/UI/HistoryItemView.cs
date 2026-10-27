using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Fits history cards to their text while preserving the speaker column.</summary>
[AddComponentMenu("VNovelizer/UI/History Item View")]
public sealed class HistoryItemView : MonoBehaviour
{
    [SerializeField] private TMP_Text speakerLabel;
    [SerializeField] private TMP_Text dialogueLabel;
    [SerializeField] private LayoutElement itemLayout;
    private float lastWidth=-1;
    private string lastText;
    private bool refreshing;

    public void Bind(HistoryEntry entry)
    {
        if(speakerLabel!=null) speakerLabel.text=string.IsNullOrWhiteSpace(entry?.Speaker)?"旁白":entry.Speaker;
        if(dialogueLabel!=null) { dialogueLabel.text=entry?.Text??string.Empty; dialogueLabel.maxVisibleCharacters=int.MaxValue; }
        lastWidth=-1;
        RefreshHeight();
    }
    private void OnEnable() { lastWidth=-1; RefreshHeight(); }
    private void OnRectTransformDimensionsChange() { RefreshHeight(); }
    public void RefreshHeight()
    {
        if(refreshing || dialogueLabel==null || itemLayout==null) return;
        float width=dialogueLabel.rectTransform.rect.width;
        if(width<1) return;
        string text=dialogueLabel.text;
        if(Mathf.Abs(width-lastWidth)<.5f && text==lastText) return;
        refreshing=true;
        try {
            lastWidth=width; lastText=text;
            float textHeight=Mathf.Ceil(dialogueLabel.GetPreferredValues(text,width,0).y);
            float height=Mathf.Max(156,textHeight+62);
            if(Mathf.Abs(itemLayout.preferredHeight-height)>.5f) itemLayout.preferredHeight=height;
        } finally { refreshing=false; }
    }
}