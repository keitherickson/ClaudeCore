namespace ClaudeCore.Services;

/// <summary>UI branding read from configuration ("Branding" section).</summary>
public sealed class BrandingOptions
{
    public const string SectionName = "Branding";

    /// <summary>Display name shown in the navbar, page title, and footer.</summary>
    public string AppName { get; set; } = "KeithVision";
}
