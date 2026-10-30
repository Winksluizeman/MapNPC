using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

// ---------- Verhaaldata (uit theo_story.json) ----------
[Serializable] public class StoryChoice { public int id; public string conditie; public string naarBlok; public string[] trefwoorden; }
[Serializable] public class StoryBlock { public string naam; public string doel; public StoryChoice[] keuzes; }
[Serializable] public class TheoStory
{
    public string naam, beschrijving, praatwijze, startBlok;
    public string[] voorbeeldzinnen, regels;
    public StoryBlock[] blokken;
}

// ---------- Ollama-API ----------
[Serializable] public class OllamaMessage { public string role; public string content; }
[Serializable] public class OllamaOptions { public float temperature; }
[Serializable] public class OllamaRequest
{
    public string model;
    public OllamaMessage[] messages;
    public bool stream;
    public string format;
    public OllamaOptions options;
}
[Serializable] public class OllamaResponse { public OllamaMessage message; }

// Wat het LLM teruggeeft bij het antwoord
[Serializable] public class TheoResult { public string antwoord; public string emotie; }

/// <summary>
/// Eigen versie van Convai's Narrative Design + State of Mind.
/// Per beurt twee stappen:
///   1. Beoordelen: aparte aanroep (temperature 0) kiest welke overgang past bij het laatste bericht van de speler.
///   2. Antwoorden: Theo antwoordt met het doel van het (eventueel nieuwe) blok, plus zijn emotie.
/// </summary>
public class TheoBrain : MonoBehaviour
{
    [Header("Personage")]
    [SerializeField] TextAsset storyJson;
    [SerializeField] bool greetOnStart = true;
    [Tooltip("Voorbeeldzinnen in de prompt. Uit voor kleine modellen: die gaan ze letterlijk herhalen.")]
    [SerializeField] bool useExampleLines = false;

    [Header("Ollama")]
    [SerializeField] string ollamaUrl = "http://localhost:11434/api/chat";
    [SerializeField] string model = "gemma3:4b";
    [SerializeField, Range(0f, 1.5f)] float temperature = 0.7f;
    [SerializeField] int timeoutSeconds = 120;

    [Header("Events")]
    public UnityEvent<string> onAnswer;
    public UnityEvent<string> onEmotion;
    public UnityEvent<string> onBlockChanged;
    public UnityEvent<bool> onBusyChanged;

    static readonly string[] Emoties =
    {
        "vreugde", "vertrouwen", "angst", "verrassing", "verdriet", "afkeer", "boosheid", "verwachting",
        "sereniteit", "interesse", "peinzend"
    };

    TheoStory story;
    StoryBlock currentBlock;
    readonly List<OllamaMessage> history = new List<OllamaMessage>();
    string lastTheoLine = "";
    string logPath;

    public bool IsBusy { get; private set; }
    public string CurrentBlockName => currentBlock?.naam;

    void Start()
    {
        story = JsonUtility.FromJson<TheoStory>(storyJson.text);
        SetBlock(story.startBlok);

        logPath = Path.Combine(Application.persistentDataPath,
            $"theo_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        File.WriteAllText(logPath,
            "tijd;personage;model;blok_voor;keuze;methode;blok_na;emotie;beoordeling_ms;totaal_ms;speler;theo\n");
        Debug.Log($"[TheoBrain] Log: {logPath}");

        if (greetOnStart) Send("(De speler komt aanlopen en staat nu bij je.)", isGreeting: true);
    }

    public void Send(string playerText) => Send(playerText, false);

    void Send(string playerText, bool isGreeting)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(playerText)) return;
        StartCoroutine(Turn(playerText, isGreeting));
    }

    IEnumerator Turn(string playerText, bool isGreeting)
    {
        SetBusy(true);
        var total = System.Diagnostics.Stopwatch.StartNew();
        string blockBefore = currentBlock.naam;
        int choice = 0;
        long classifyMs = 0;

        // ---------- Stap 1: beoordelen welke overgang past ----------
        // Eerst vaste trefwoorden (snel en betrouwbaar), anders het LLM
        string method = "-";
        bool hasChoices = currentBlock.keuzes != null && currentBlock.keuzes.Length > 0;
        if (!isGreeting && hasChoices)
        {
            choice = MatchKeywords(playerText);
            if (choice != 0)
            {
                method = "trefwoord";
                Debug.Log($"[TheoBrain] Trefwoord gevonden: keuze {choice}");
            }
            else
            {
                method = "llm";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string classifyContent = null;
                var classifyMessages = new[]
                {
                    new OllamaMessage { role = "system", content = BuildClassifierPrompt() },
                    new OllamaMessage { role = "user", content =
                        $"Laatste zin van {story.naam}: \"{lastTheoLine}\"\nLaatste bericht van de speler: \"{playerText}\"" }
                };
                yield return PostChat(classifyMessages, 0f, c => classifyContent = c);
                sw.Stop();
                classifyMs = sw.ElapsedMilliseconds;
                Debug.Log($"[TheoBrain] Beoordeling LLM: {classifyContent}");
                choice = ParseChoice(classifyContent);
            }

            foreach (var c in currentBlock.keuzes)
                if (c.id == choice) { SetBlock(c.naarBlok); break; }
        }

        // ---------- Stap 2: Theo antwoordt in het (nieuwe) blok ----------
        var messages = new List<OllamaMessage> { new OllamaMessage { role = "system", content = BuildCharacterPrompt() } };
        messages.AddRange(history);
        messages.Add(new OllamaMessage { role = "user", content = playerText });

        string content = null;
        yield return PostChat(messages.ToArray(), temperature, c => content = c);

        if (content == null)
        {
            onAnswer?.Invoke("(Theo kan even niet antwoorden. Draait Ollama?)");
            SetBusy(false);
            yield break;
        }

        TheoResult result = ParseAnswer(content);
        total.Stop();

        // Geschiedenis: alleen de gesproken tekst, zodat het gesprek natuurlijk blijft
        history.Add(new OllamaMessage { role = "user", content = playerText });
        history.Add(new OllamaMessage { role = "assistant", content = result.antwoord });
        lastTheoLine = result.antwoord;

        onAnswer?.Invoke(result.antwoord);
        onEmotion?.Invoke(result.emotie);

        if (!isGreeting)
            Log(blockBefore, choice, method, currentBlock.naam, result, classifyMs, total.ElapsedMilliseconds, playerText);

        SetBusy(false);
    }

    // ---------- Prompts ----------

    string BuildClassifierPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Je beoordeelt een gesprek tussen een speler en het personage {story.naam}.");
        sb.AppendLine("Kijk ALLEEN naar het laatste bericht van de speler. Welke situatie past daar het best bij?");
        sb.AppendLine();
        foreach (var c in currentBlock.keuzes) sb.AppendLine($"{c.id}: {c.conditie}");
        sb.AppendLine("0: geen van deze");
        sb.AppendLine();
        sb.AppendLine("Antwoord alleen met JSON in deze vorm: {\"keuze\": 0}");
        return sb.ToString();
    }

    string BuildCharacterPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Je speelt het personage {story.naam}. Blijf altijd in je rol.");
        sb.AppendLine();
        sb.AppendLine("BESCHRIJVING:");
        sb.AppendLine(story.beschrijving);
        sb.AppendLine();
        sb.AppendLine("PRAATWIJZE:");
        sb.AppendLine(story.praatwijze);
        if (useExampleLines && story.voorbeeldzinnen != null && story.voorbeeldzinnen.Length > 0)
        {
            sb.AppendLine("Voorbeeldzinnen (alleen ter inspiratie voor de toon, niet letterlijk herhalen):");
            foreach (var z in story.voorbeeldzinnen) sb.AppendLine($"- \"{z}\"");
        }
        sb.AppendLine();
        sb.AppendLine("REGELS:");
        foreach (var r in story.regels) sb.AppendLine($"- {r}");
        sb.AppendLine("- Herhaal geen zinnen of uitdrukkingen die je eerder in dit gesprek al gebruikte.");
        sb.AppendLine("- Reageer altijd direct op wat de speler als laatste zei. Beantwoord een vraag eerst, voordat je over iets anders begint.");
        sb.AppendLine();
        sb.AppendLine($"HUIDIGE FASE VAN HET GESPREK: {currentBlock.naam}");
        sb.AppendLine($"DOEL IN DEZE FASE: {currentBlock.doel}");
        sb.AppendLine();
        sb.AppendLine("Antwoord ALLEEN met JSON in precies deze vorm:");
        sb.AppendLine("{\"antwoord\": \"wat Theo hardop zegt\", \"emotie\": \"een emotie\"}");
        sb.AppendLine($"Kies de emotie uit: {string.Join(", ", Emoties)}. De emotie is wat Theo voelt bij dit antwoord.");
        return sb.ToString();
    }

    // ---------- Ollama ----------

    IEnumerator PostChat(OllamaMessage[] messages, float temp, Action<string> onDone)
    {
        var body = new OllamaRequest
        {
            model = model,
            messages = messages,
            stream = false,
            format = "json",
            options = new OllamaOptions { temperature = temp }
        };

        using var req = new UnityWebRequest(ollamaUrl, "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSeconds;
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[TheoBrain] Ollama-fout: {req.error}\n{req.downloadHandler.text}");
            onDone(null);
            yield break;
        }

        try { onDone(JsonUtility.FromJson<OllamaResponse>(req.downloadHandler.text).message.content); }
        catch (Exception e)
        {
            Debug.LogError($"[TheoBrain] Onverwacht antwoord van Ollama ({e.Message}): {req.downloadHandler.text}");
            onDone(null);
        }
    }

    // ---------- Uitlezen ----------

    int MatchKeywords(string text)
    {
        string t = text.ToLowerInvariant();
        foreach (var c in currentBlock.keuzes)
        {
            if (c.trefwoorden == null) continue;
            foreach (var w in c.trefwoorden)
            {
                if (string.IsNullOrWhiteSpace(w)) continue;
                // Woordgrenzen: "dag" past niet op "vandaag"
                if (Regex.IsMatch(t, $@"\b{Regex.Escape(w.ToLowerInvariant())}\b")) return c.id;
            }
        }
        return 0;
    }

    static int ParseChoice(string content)
    {
        if (string.IsNullOrEmpty(content)) return 0;
        // Werkt voor {"keuze": 2} en voor {"keuze": "2"}
        var m = Regex.Match(content, "\"keuze\"\\s*:\\s*\"?(\\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int n)) return n;
        Debug.LogWarning($"[TheoBrain] Geen keuze gevonden in: {content}");
        return 0;
    }

    TheoResult ParseAnswer(string content)
    {
        try
        {
            var r = JsonUtility.FromJson<TheoResult>(content);
            if (string.IsNullOrWhiteSpace(r.antwoord)) throw new Exception("leeg antwoord");
            r.emotie = (r.emotie ?? "").Trim().ToLower();
            if (Array.IndexOf(Emoties, r.emotie) < 0) r.emotie = "onbekend";
            return r;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[TheoBrain] Kon JSON niet lezen ({e.Message}): {content}");
            return new TheoResult { antwoord = content, emotie = "onbekend" };
        }
    }

    // ---------- Overig ----------

    void SetBlock(string name)
    {
        foreach (var b in story.blokken)
        {
            if (b.naam != name) continue;
            currentBlock = b;
            Debug.Log($"[TheoBrain] Blok: {name}");
            onBlockChanged?.Invoke(name);
            return;
        }
        Debug.LogError($"[TheoBrain] Blok '{name}' bestaat niet in de JSON.");
    }

    void SetBusy(bool busy)
    {
        IsBusy = busy;
        onBusyChanged?.Invoke(busy);
    }

    void Log(string before, int choice, string method, string after, TheoResult r, long classifyMs, long totalMs, string player)
    {
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\n", " ") + "\"";
        File.AppendAllText(logPath,
            $"{DateTime.Now:HH:mm:ss};{Q(storyJson.name)};{model};{before};{choice};{method};{after};{r.emotie};" +
            $"{classifyMs};{totalMs};{Q(player)};{Q(r.antwoord)}\n");
    }
}