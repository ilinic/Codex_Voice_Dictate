using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexVoiceDictate;

internal sealed class OpenAiSpeechClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private const string TranscriptionUrl = "https://api.openai.com/v1/audio/transcriptions";
    private const string ResponsesUrl = "https://api.openai.com/v1/responses";

    public async Task<string> TranscribeAsync(byte[] wav, string apiKey, AppSettings settings, CancellationToken cancellation)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("gpt-transcribe"), "model");
        content.Add(new StringContent("json"), "response_format");
        content.Add(new StringContent("Software-development voice dictation. Preserve the original language, including technical identifiers and code-switching. Do not translate or answer the speaker."), "prompt");
        foreach (string language in settings.SelectedLanguages)
            content.Add(new StringContent(language), "languages[]");
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "file", "dictation.wav");

        using var request = NewRequest(TranscriptionUrl, apiKey);
        request.Content = content;
        using var response = await Http.SendAsync(request, cancellation).ConfigureAwait(false);
        JsonNode root = await ReadResult(response, cancellation).ConfigureAwait(false);
        string? text = root["text"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text)) throw new IOException("The transcription service returned no text.");
        return text.Trim();
    }

    public async Task<string> CorrectAsync(string raw, string apiKey, AppSettings settings, CancellationToken cancellation)
    {
        string effort = AppSettings.CorrectionModels.FirstOrDefault(m => m.Id == settings.CorrectionModel).Effort ?? "none";
        var payload = new
        {
            model = settings.CorrectionModel,
            store = false,
            max_output_tokens = 2048,
            reasoning = new { effort },
            instructions = "You edit software-development speech transcriptions. Return ONLY the corrected utterance, without quotes, explanation, or answers. Fix obvious transcription errors, spelling, punctuation, and programming terms. Keep the speaker's language, tone, meaning and code-switching. Do not invent facts or translate.",
            input = raw
        };
        using var request = NewRequest(ResponsesUrl, apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, cancellation).ConfigureAwait(false);
        JsonNode root = await ReadResult(response, cancellation).ConfigureAwait(false);
        var result = new StringBuilder();
        if (root["output"] is JsonArray items)
            foreach (JsonNode? item in items)
                if (item?["type"]?.ToString() == "message" && item["content"] is JsonArray blocks)
                    foreach (JsonNode? block in blocks)
                        if (block?["type"]?.ToString() == "output_text") result.Append(block["text"]?.ToString());
        if (string.IsNullOrWhiteSpace(result.ToString())) throw new IOException("Correction returned no text.");
        return result.ToString().Trim();
    }

    private static HttpRequestMessage NewRequest(string url, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static async Task<JsonNode> ReadResult(HttpResponseMessage response, CancellationToken cancellation)
    {
        string body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = "";
            try { detail = JsonNode.Parse(body)?["error"]?["message"]?.ToString() ?? ""; }
            catch (JsonException) { /* No raw API response in the UI or logs. */ }
            if (detail.Length > 240) detail = detail[..240];
            throw new IOException($"OpenAI HTTP {(int)response.StatusCode}: {detail}");
        }
        try { return JsonNode.Parse(body) ?? throw new JsonException("Empty response"); }
        catch (JsonException error) { throw new IOException("OpenAI returned invalid JSON.", error); }
    }
}
