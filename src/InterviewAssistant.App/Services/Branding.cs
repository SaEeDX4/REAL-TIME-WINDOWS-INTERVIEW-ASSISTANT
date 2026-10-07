using System.Text.Json;

namespace InterviewAssistant.App.Services;

/// <summary>
/// Product identity in one place. Defaults are a neutral placeholder until the commercial brand is chosen;
/// override without recompiling via branding.json next to the EXE: {"ProductName": "...", "CompanyName": "...", "SupportUrl": "..."}.
/// </summary>
public static class Branding
{
    public static string ProductName { get; private set; } = "Interview Assistant";
    public static string CompanyName { get; private set; } = "Interview Assistant";
    public static string SupportUrl { get; private set; } = "";

    public static void Load(string baseDir)
    {
        var path = Path.Combine(baseDir, "branding.json");
        if (!File.Exists(path)) return;
        try
        {
            var b = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
            ProductName = b.GetValueOrDefault("ProductName", ProductName);
            CompanyName = b.GetValueOrDefault("CompanyName", CompanyName);
            SupportUrl = b.GetValueOrDefault("SupportUrl", SupportUrl);
        }
        catch (Exception ex) when (ex is JsonException or IOException) { AppLog.Warn("branding.json ignored: " + ex.Message); }
    }
}
