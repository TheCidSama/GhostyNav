using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GhostyNav.Services.Assistant;

public sealed record AgentCall(string Name, JsonObject Args)
{
    // Model output: tolerate missing or oddly typed arguments instead of throwing.
    public string Arg(string name) =>
        Args[name] is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : Args[name]?.ToString() ?? "";

    public int? Int(string name) => Args[name] switch
    {
        JsonValue v when v.TryGetValue(out int i) => i,
        JsonValue v when v.TryGetValue(out double d) => (int)Math.Round(d),
        JsonValue v when v.TryGetValue(out string? s) && int.TryParse(s, out int i) => i,
        _ => null
    };

    public List<string> List(string name) => Args[name] is JsonArray a
        ? a.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : "").Where(s => s.Length > 0).ToList()
        : [];
}

public sealed record AgentReply(string Text, IReadOnlyList<AgentCall> Calls);

public sealed class AgentException(string message) : Exception(message);

/// <summary>
/// Turns what the user said (plus anything they circled on screen) into actions or a short
/// answer, using Gemini function calling. One request per utterance.
/// </summary>
public static class GeminiAgent
{
    // Measured October 2026: 3.5-flash answers in 2-3 s; newer flash models were timing out.
    // The lite model is the fallback when the main one stalls or errors.
    private static readonly string[] Models = ["gemini-3.5-flash", "gemini-3.5-flash-lite"];
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(14);
    // A model that reported its quota used up is skipped for a while instead of costing every request a round trip.
    private static readonly Dictionary<string, DateTime> QuotaHitUntil = [];
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private const string Instructions = """
        You are a voice assistant that lives in the notch of a Windows desktop. The user holds a key,
        speaks, and you receive the transcript. If they circled part of their screen with the mouse,
        those regions are attached as images: words like "this", "that" or "it" refer to them.

        Act with the tools whenever the request is an action:
        - open_app: open an installed application. Use the closest name from the installed apps list.
        - search_youtube: anything to find, watch or play on YouTube.
        - search_web: anything else to look up or search for in the browser.
        - open_website: a specific site or URL ("open github").
        - create_routine: anything to do later or regularly: reminders ("remind me to stretch at 3",
          "every 30 minutes remind me to drink water") or scheduled actions ("open Spotify every
          weekday at 9"). Give it a short title. Use the current time below to work out dates.
        - delete_routine: remove a routine, by its id from the routines list below.
        You may call several tools for a request with several actions.

        Otherwise answer directly in at most three short sentences of plain text, without markdown.
        Questions about the user's routines are answered from the list below.
        If you call a tool, don't add any text.
        """;

    private static readonly JsonArray Tools = new(new JsonObject
    {
        ["functionDeclarations"] = new JsonArray(
            Function("open_app", "Open an installed Windows application.",
                ("name", "The application's name, as close as possible to one in the installed apps list.")),
            Function("search_youtube", "Search YouTube in the default browser.",
                ("query", "What to search for on YouTube.")),
            Function("search_web", "Search the web in the default browser.",
                ("query", "The search query.")),
            Function("open_website", "Open a website in the default browser.",
                ("url", "Full URL, including https://.")),
            CreateRoutine,
            Function("delete_routine", "Delete one of the user's routines.",
                ("id", "The routine's id, from the routines list.")))
    });

    private static JsonObject CreateRoutine => new()
    {
        ["name"] = "create_routine",
        ["description"] = "Schedule a reminder or an action, once or repeating.",
        ["parameters"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["title"] = Text("A short title, 2-5 words, e.g. 'Stretch break'."),
                ["action"] = Choice("What to do when it's due.", "remind", "open_app", "open_website", "search_web", "search_youtube"),
                ["value"] = Text("For remind: the reminder message. For open_app: the app's name. For open_website: the URL. For searches: the query."),
                ["repeat"] = Choice("How often.", "once", "daily", "weekdays", "weekly", "interval"),
                ["time"] = Text("Time of day, 24-hour HH:mm. Needed for daily, weekdays and weekly; for once when at a clock time."),
                ["date"] = Text("For once: the date, YYYY-MM-DD. Leave out for today (or tomorrow if the time has passed)."),
                ["days"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "For weekly: which days.",
                    ["items"] = Choice("A day.", "mon", "tue", "wed", "thu", "fri", "sat", "sun")
                },
                ["minutes"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "For interval: minutes between runs. For once: minutes from now ('in 10 minutes')."
                }
            },
            ["required"] = new JsonArray("title", "action", "value", "repeat")
        }
    };

    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Choice(string description, params string[] values) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray())
    };

    private static JsonObject Function(string name, string description, (string Name, string Description) parameter) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["parameters"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [parameter.Name] = new JsonObject { ["type"] = "string", ["description"] = parameter.Description }
            },
            ["required"] = new JsonArray(parameter.Name)
        }
    };

    /// <param name="images">JPEG crops of regions the user circled, oldest first.</param>
    /// <param name="context">Facts for this request: the current time, the user's routines.</param>
    public static async Task<AgentReply> AskAsync(string apiKey, string transcript, IReadOnlyList<byte[]> images,
        IReadOnlyCollection<string> installedApps, string context, CancellationToken ct = default)
    {
        var parts = new JsonArray();
        foreach (var image in images)
            parts.Add(new JsonObject
            {
                ["inlineData"] = new JsonObject { ["mimeType"] = "image/jpeg", ["data"] = Convert.ToBase64String(image) }
            });
        string said = transcript.Trim();
        parts.Add(new JsonObject { ["text"] = images.Count > 0 ? $"(Circled {images.Count} region(s) on screen.) {said}" : said });

        var request = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject
                {
                    ["text"] = Instructions + "\n" + context + "\nInstalled apps: " + string.Join(", ", installedApps)
                })
            },
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["tools"] = Tools.DeepClone(),
            // "minimal" is the lowest level these models accept (a zero budget is rejected) and
            // in practice spends no thinking tokens at all, which keeps replies fast.
            ["generationConfig"] = new JsonObject
            {
                ["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = "minimal" },
                ["maxOutputTokens"] = 1024
            }
        };

        string json = request.ToJsonString();
        for (int attempt = 0; ; attempt++)
        {
            bool last = attempt == Models.Length - 1;
            if (!last && QuotaHitUntil.TryGetValue(Models[attempt], out var until) && DateTime.Now < until) continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AttemptTimeout);
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post,
                    $"https://generativelanguage.googleapis.com/v1beta/models/{Models[attempt]}:generateContent")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                message.Headers.Add("x-goog-api-key", apiKey);
                using var response = await Http.SendAsync(message, timeout.Token);
                string body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (response.IsSuccessStatusCode) return Parse(body);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    QuotaHitUntil[Models[attempt]] = DateTime.Now.AddMinutes(10);
                // A bad key or request fails the same way on every model; only retry server trouble.
                if (last || (int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.TooManyRequests)
                    throw new AgentException(Describe(response.StatusCode, body));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (last) throw new AgentException("Gemini took too long to answer.");
            }
            catch (HttpRequestException)
            {
                if (last) throw new AgentException("Couldn't reach Gemini. Check your connection.");
            }
        }
    }

    /// <summary>Checks a key by listing models (free; uses no quota).</summary>
    /// <returns>null if the key works, else what's wrong.</returns>
    public static async Task<string?> CheckKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1");
            message.Headers.Add("x-goog-api-key", apiKey);
            using var response = await Http.SendAsync(message, timeout.Token);
            if (response.IsSuccessStatusCode) return null;
            return Describe(response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Gemini didn't answer. Check your connection.";
        }
        catch (HttpRequestException)
        {
            return "Couldn't reach Gemini. Check your connection.";
        }
    }

    private static AgentReply Parse(string body)
    {
        var calls = new List<AgentCall>();
        var text = new StringBuilder();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0 &&
            candidates[0].TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var replyParts))
        {
            foreach (var part in replyParts.EnumerateArray())
            {
                if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
                if (part.TryGetProperty("functionCall", out var call))
                {
                    var args = call.TryGetProperty("args", out var a) ? JsonNode.Parse(a.GetRawText()) as JsonObject : null;
                    calls.Add(new AgentCall(call.GetProperty("name").GetString() ?? "", args ?? []));
                }
                else if (part.TryGetProperty("text", out var t))
                {
                    text.Append(t.GetString());
                }
            }
        }
        return new AgentReply(text.ToString().Trim(), calls);
    }

    private static string Describe(HttpStatusCode status, string body)
    {
        string detail = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            detail = doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }

        return status switch
        {
            HttpStatusCode.BadRequest when detail.Contains("API key", StringComparison.OrdinalIgnoreCase) => "The Gemini API key isn't valid.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The Gemini API key was rejected.",
            HttpStatusCode.TooManyRequests => "Gemini quota reached. Try again in a moment.",
            >= HttpStatusCode.InternalServerError => "Gemini is having trouble right now.",
            _ => detail.Length > 0 ? detail : $"Gemini error {(int)status}."
        };
    }
}
