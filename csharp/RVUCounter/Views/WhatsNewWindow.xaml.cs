using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using RVUCounter.Core;
using Serilog;

namespace RVUCounter.Views;

/// <summary>
/// Window for displaying What's New / release notes.
/// Ported from Python whats_new_window.py.
/// </summary>
public partial class WhatsNewWindow : Window
{
    public new string Title { get; set; }

    public WhatsNewWindow(string? version = null)
    {
        InitializeComponent();

        // Apply dark title bar based on current theme
        ThemeManager.ApplyCurrentThemeTitleBar(this);

        DataContext = this;

        version ??= Config.AppVersion.Split(' ')[0];
        Title = $"What's New in RVU Counter {version}";

        LoadContent(version);
    }

    private async void LoadContent(string version)
    {
        try
        {
            var docManager = new DocManager();
            var content = await docManager.GetWhatsNewAsync(version);

            if (!string.IsNullOrEmpty(content))
            {
                ContentTextBox.Text = FormatMarkdown(content);
            }
            else
            {
                ContentTextBox.Text = $"What's New content not found for version {version}.\n\n" +
                                     "Please check the documentation folder.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading What's New content");
            ContentTextBox.Text = $"Error loading content: {ex.Message}";
        }
    }

    /// <summary>
    /// Basic markdown formatting for text display.
    /// </summary>
    private string FormatMarkdown(string mdText)
    {
        var lines = mdText.Split('\n');
        var formatted = new StringBuilder();

        foreach (var line in lines)
        {
            // Headers
            if (line.StartsWith("# "))
            {
                formatted.AppendLine();
                formatted.AppendLine(line[2..].ToUpperInvariant());
                formatted.AppendLine(new string('=', 50));
            }
            else if (line.StartsWith("## "))
            {
                formatted.AppendLine();
                formatted.AppendLine(line[3..].ToUpperInvariant());
                formatted.AppendLine(new string('-', 40));
            }
            else if (line.StartsWith("### "))
            {
                formatted.AppendLine();
                formatted.AppendLine(line[4..]);
            }
            // Lists
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                formatted.AppendLine("  \u2022 " + line[2..]);
            }
            // Checkmarks
            else if (line.Trim().StartsWith("\u2705"))
            {
                formatted.AppendLine("  " + line.Trim());
            }
            // Regular lines
            else
            {
                formatted.AppendLine(line);
            }
        }

        return formatted.ToString();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
