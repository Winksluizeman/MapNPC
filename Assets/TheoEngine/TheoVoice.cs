using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

[Serializable] public class PiperRequest { public string text; public float length_scale; }

/// <summary>
/// Laat Theo praten: stuurt elk antwoord naar de lokale Piper-server (/synthesize),
/// zet de WAV om naar een AudioClip en speelt die af.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class TheoVoice : MonoBehaviour
{
    [SerializeField] TheoBrain brain;
    [SerializeField] string piperUrl = "http://localhost:5000/synthesize";
    [Tooltip("Spreeksnelheid: hoger is langzamer. Theo praat rustig, dus iets boven 1.")]
    [SerializeField, Range(0.5f, 2f)] float lengthScale = 1.1f;
    [SerializeField] bool voiceEnabled = true;
    [SerializeField] int timeoutSeconds = 30;

    AudioSource source;
    int requestCounter;

    void Awake()
    {
        source = GetComponent<AudioSource>();
        brain.onAnswer.AddListener(Speak);
    }

    public void Speak(string text)
    {
        // Foutmeldingen van TheoBrain staan tussen haakjes; die spreken we niet uit
        if (!voiceEnabled || string.IsNullOrWhiteSpace(text) || text.StartsWith("(")) return;
        StartCoroutine(SpeakRoutine(text, ++requestCounter));
    }

    IEnumerator SpeakRoutine(string text, int id)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var body = new PiperRequest { text = text, length_scale = lengthScale };

        using var req = new UnityWebRequest(piperUrl, "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSeconds;
        yield return req.SendWebRequest();
        sw.Stop();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[TheoVoice] Piper-fout: {req.error}. Draait de Piper-server op {piperUrl}?");
            yield break;
        }

        // Er kwam al een nieuwer antwoord binnen: dit oude niet meer afspelen
        if (id != requestCounter) yield break;

        AudioClip clip;
        try { clip = WavToClip(req.downloadHandler.data, $"theo_{id}"); }
        catch (Exception e)
        {
            Debug.LogError($"[TheoVoice] Kon WAV niet lezen: {e.Message}");
            yield break;
        }

        source.Stop();
        source.clip = clip;
        source.Play();
        Debug.Log($"[TheoVoice] Stem gemaakt in {sw.ElapsedMilliseconds} ms ({clip.length:0.0} s audio, {text.Length} tekens)");
    }

    /// <summary>Zet een 16-bit PCM WAV om naar een AudioClip.</summary>
    static AudioClip WavToClip(byte[] wav, string name)
    {
        if (wav == null || wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF")
            throw new Exception("geen WAV-bestand ontvangen");

        int channels = 1, sampleRate = 22050, bits = 16;
        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            string chunkId = Encoding.ASCII.GetString(wav, pos, 4);
            int size = BitConverter.ToInt32(wav, pos + 4);
            int start = pos + 8;

            if (chunkId == "fmt ")
            {
                channels = BitConverter.ToInt16(wav, start + 2);
                sampleRate = BitConverter.ToInt32(wav, start + 4);
                bits = BitConverter.ToInt16(wav, start + 14);
            }
            else if (chunkId == "data")
            {
                if (bits != 16) throw new Exception($"{bits}-bit audio wordt niet ondersteund");
                // Bij gestreamde WAV's staat de grootte soms niet goed in de header
                if (size <= 0 || start + size > wav.Length) size = wav.Length - start;

                int samples = size / 2;
                var data = new float[samples];
                for (int i = 0; i < samples; i++)
                    data[i] = BitConverter.ToInt16(wav, start + i * 2) / 32768f;

                var clip = AudioClip.Create(name, samples / channels, channels, sampleRate, false);
                clip.SetData(data, 0);
                return clip;
            }

            pos = start + size + (size % 2);
        }
        throw new Exception("geen audiodata gevonden");
    }
}