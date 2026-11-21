using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using VOS.Core.Settings;

namespace VOS.Core.Translate;

public sealed record TranslationResult(string Text, string SourceLanguage, string Provider);

/// <summary>
/// Online translation through the provider the user chose (MyMemory without a key, DeepL or LibreTranslate with
/// theirs). Callers must check <see cref="PrivacySettings.OnlineTranslationConsent"/> first: text is sent to the
/// provider. Long text is split on sentence boundaries to stay within provider limits.
/// </summary>
public static class Translator
{
    private static readonly HttpClient s_http = Create();

    private static HttpClient Create()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("V-OS", AppInfo.Version));
        return h;
    }

    public static async Task<TranslationResult> TranslateAsync(string text, string source, string target, TranslateSettings s, CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) return new TranslationResult("", source, s.Provider);
        var detected = source == "auto" ? Detect(text) : source;
        if (detected == target) return new TranslationResult(text, detected, s.Provider);
        switch (s.Provider)
        {
            case "deepl" when !string.IsNullOrWhiteSpace(s.DeepLKey):
                return await DeepLAsync(text, source, target, s.DeepLKey!, ct).ConfigureAwait(false);
            case "libretranslate":
                return await LibreAsync(text, source, target, s, ct).ConfigureAwait(false);
            default:
                var parts = new List<string>();
                foreach (var chunk in Chunks(text, 450))
                    parts.Add(await MyMemoryAsync(chunk, detected, target, ct).ConfigureAwait(false));
                return new TranslationResult(string.Join(" ", parts), detected, "MyMemory");
        }
    }

    private static async Task<string> MyMemoryAsync(string text, string source, string target, CancellationToken ct)
    {
        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={source}|{target}";
        using var doc = JsonDocument.Parse(await s_http.GetStringAsync(url, ct).ConfigureAwait(false));
        var root = doc.RootElement;
        int status = root.TryGetProperty("responseStatus", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : int.TryParse(st.ToString(), out var n) ? n : 0;
        var translated = root.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";
        if (status != 200) throw new HttpRequestException(translated.Length > 0 ? translated : "The translation service declined the request.");
        return System.Net.WebUtility.HtmlDecode(translated);
    }

    private static async Task<TranslationResult> DeepLAsync(string text, string source, string target, string key, CancellationToken ct)
    {
        var host = key.EndsWith(":fx", StringComparison.Ordinal) ? "https://api-free.deepl.com" : "https://api.deepl.com";
        using var req = new HttpRequestMessage(HttpMethod.Post, host + "/v2/translate");
        req.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
        var form = new List<KeyValuePair<string, string>> { new("text", text), new("target_lang", target.ToUpperInvariant() == "EN" ? "EN-US" : target.ToUpperInvariant()) };
        if (source != "auto") form.Add(new("source_lang", source.ToUpperInvariant()));
        req.Content = new FormUrlEncodedContent(form);
        using var res = await s_http.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var t = doc.RootElement.GetProperty("translations")[0];
        return new TranslationResult(t.GetProperty("text").GetString() ?? "", (t.TryGetProperty("detected_source_language", out var d) ? d.GetString() ?? source : source).ToLowerInvariant(), "DeepL");
    }

    private static async Task<TranslationResult> LibreAsync(string text, string source, string target, TranslateSettings s, CancellationToken ct)
    {
        var body = new Dictionary<string, string> { ["q"] = text, ["source"] = source, ["target"] = target, ["format"] = "text" };
        if (!string.IsNullOrWhiteSpace(s.LibreTranslateKey)) body["api_key"] = s.LibreTranslateKey!;
        using var res = await s_http.PostAsJsonAsync(s.LibreTranslateUrl.TrimEnd('/') + "/translate", body, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var detected = doc.RootElement.TryGetProperty("detectedLanguage", out var dl) && dl.TryGetProperty("language", out var lang) ? lang.GetString() ?? source : source;
        return new TranslationResult(doc.RootElement.GetProperty("translatedText").GetString() ?? "", detected, "LibreTranslate");
    }

    /// <summary>Script-based language guess (good enough to pick a source for providers that need one).</summary>
    public static string Detect(string text)
    {
        int cyr = 0, latin = 0, cjk = 0, kana = 0, hangul = 0, arabic = 0, hebrew = 0, greek = 0, devanagari = 0, ukr = 0, ro = 0, de = 0, fr = 0, es = 0;
        foreach (var c in text)
        {
            if (c is >= 'Ѐ' and <= 'ӿ') { cyr++; if (c is 'і' or 'ї' or 'є' or 'ґ' or 'І' or 'Ї' or 'Є' or 'Ґ') ukr++; }
            else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 'À' and <= 'ɏ')
            {
                latin++;
                if (c is 'ă' or 'â' or 'î' or 'ș' or 'ț' or 'ş' or 'ţ') ro++;
                if (c is 'ä' or 'ö' or 'ü' or 'ß') de++;
                if (c is 'é' or 'è' or 'ê' or 'à' or 'ç' or 'œ') fr++;
                if (c is 'ñ' or '¿' or '¡' or 'á' or 'ó' or 'í' or 'ú') es++;
            }
            else if (c is >= '぀' and <= 'ヿ') kana++;
            else if (c is >= '一' and <= '鿿') cjk++;
            else if (c is >= '가' and <= '힯') hangul++;
            else if (c is >= '؀' and <= 'ۿ') arabic++;
            else if (c is >= '֐' and <= '׿') hebrew++;
            else if (c is >= 'Ͱ' and <= 'Ͽ') greek++;
            else if (c is >= 'ऀ' and <= 'ॿ') devanagari++;
        }
        var scripts = new (int n, string lang)[] { (cyr, ukr > 0 ? "uk" : "ru"), (kana, "ja"), (cjk, kana > 0 ? "ja" : "zh"), (hangul, "ko"), (arabic, "ar"), (hebrew, "he"), (greek, "el"), (devanagari, "hi") };
        var best = scripts.MaxBy(x => x.n);
        if (best.n > latin) return best.lang;
        var accents = new (int n, string lang)[] { (ro, "ro"), (de, "de"), (fr, "fr"), (es, "es") }.MaxBy(x => x.n);
        return accents.n > 0 ? accents.lang : "en";
    }

    private static IEnumerable<string> Chunks(string text, int max)
    {
        if (text.Length <= max) { yield return text; yield break; }
        var sb = new StringBuilder();
        foreach (var sentence in System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?…。])\s+"))
        {
            if (sb.Length + sentence.Length + 1 > max && sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            if (sentence.Length > max) { for (int i = 0; i < sentence.Length; i += max) yield return sentence.Substring(i, Math.Min(max, sentence.Length - i)); continue; }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(sentence);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
