using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Simpele chat-UI: invoerveld, verstuurknop, scrollbaar gesprekslog en een debugregel met blok en emotie.</summary>
public class TheoChatUI : MonoBehaviour
{
    [SerializeField] TheoBrain brain;
    [SerializeField] TMP_InputField input;
    [SerializeField] Button sendButton;
    [SerializeField] TMP_Text chatLog;
    [SerializeField] ScrollRect scrollRect;   // Scroll View waar chatLog in zit
    [SerializeField] TMP_Text debugText;

    string block = "-", emotion = "-";

    void Awake()
    {
        brain.onAnswer.AddListener(a => Append("Theo", a));
        brain.onEmotion.AddListener(e => { emotion = e; UpdateDebug(); });
        brain.onBlockChanged.AddListener(b => { block = b; UpdateDebug(); });
        brain.onBusyChanged.AddListener(busy =>
        {
            sendButton.interactable = !busy;
            input.interactable = !busy;
            UpdateDebug(busy);
        });

        sendButton.onClick.AddListener(Submit);
        input.onSubmit.AddListener(_ => Submit());
        chatLog.text = "";
    }

    void Submit()
    {
        string text = input.text.Trim();
        if (text.Length == 0 || brain.IsBusy) return;
        Append("Jij", text);
        input.text = "";
        brain.Send(text);
    }

    void Append(string who, string text)
    {
        chatLog.text += $"<b>{who}:</b> {text}\n\n";
        if (scrollRect != null) StartCoroutine(ScrollToBottom());
    }

    IEnumerator ScrollToBottom()
    {
        // Wacht een frame zodat de layout de nieuwe teksthoogte heeft berekend
        yield return null;
        Canvas.ForceUpdateCanvases();
        scrollRect.verticalNormalizedPosition = 0f;
    }

    void UpdateDebug(bool busy = false)
    {
        if (debugText == null) return;
        debugText.text = $"Blok: {block}   |   Emotie: {emotion}" + (busy ? "   |   Theo denkt na..." : "");
        if (!busy) input.ActivateInputField();
    }
}