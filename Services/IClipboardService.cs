namespace Huaxiazi.Services;

/// <summary>Application boundary for copying to and reading from the operating system clipboard.</summary>
internal interface IClipboardService
{
    void CopyText(string text);
    string GetText();
}
