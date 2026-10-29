using System.Text.Json;

namespace LmChat;

// Alles, was für LM Studio eingestellt werden kann. Leere Werte (null) = Standard des Modells.
public sealed class LlmSettings
{
    public string Server { get; set; } = "http://192.168.178.191:1234";
    public string Model { get; set; } = "gemma-4-26b-a4b-it@mxfp4";
    public string SystemPrompt { get; set; } = "";
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public double? TopK { get; set; }
    public double? MaxTokens { get; set; }
    public double? RepeatPenalty { get; set; }

    public static LlmSettings Load(ChatStore store)
    {
        var json = store.GetSetting("llm");
        if (json is null) return new LlmSettings();
        try
        {
            return JsonSerializer.Deserialize<LlmSettings>(json) ?? new LlmSettings();
        }
        catch (JsonException)
        {
            return new LlmSettings();
        }
    }

    public void Save(ChatStore store) => store.SetSetting("llm", JsonSerializer.Serialize(this));
}
