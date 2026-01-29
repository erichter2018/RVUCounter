using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using DrawColor = System.Drawing.Color;

namespace RVUCounter.Core;

/// <summary>
/// Generates the application icon programmatically.
/// Creates a professional radiology-themed icon with a stylized CT/MRI cross-section design.
/// </summary>
public static class IconGenerator
{
    /// <summary>
    /// Generate the application icon and save it to the specified path.
    /// Icon design: A circular badge with a stylized medical cross/scan imagery
    /// - Dark blue/teal background (professional, medical)
    /// - White scan lines representing CT/MRI imagery
    /// - Small chart/graph element representing RVU tracking
    /// </summary>
    public static void GenerateIcon(string outputPath)
    {
        // Create icons at multiple sizes for the .ico file
        var sizes = new[] { 16, 32, 48, 64, 128, 256 };

        using var iconStream = new MemoryStream();

        // ICO file header
        var iconImages = new List<byte[]>();

        foreach (var size in sizes)
        {
            using var bitmap = CreateIconBitmap(size);
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            iconImages.Add(ms.ToArray());
        }

        // Write ICO file
        using var output = new FileStream(outputPath, FileMode.Create);
        using var writer = new BinaryWriter(output);

        // ICO Header
        writer.Write((short)0);           // Reserved
        writer.Write((short)1);           // Type: 1 = ICO
        writer.Write((short)sizes.Length); // Number of images

        // Calculate offset (header + directory entries)
        int offset = 6 + (sizes.Length * 16);

        // Directory entries
        for (int i = 0; i < sizes.Length; i++)
        {
            var size = sizes[i];
            writer.Write((byte)(size == 256 ? 0 : size)); // Width (0 = 256)
            writer.Write((byte)(size == 256 ? 0 : size)); // Height (0 = 256)
            writer.Write((byte)0);          // Color palette
            writer.Write((byte)0);          // Reserved
            writer.Write((short)1);         // Color planes
            writer.Write((short)32);        // Bits per pixel
            writer.Write(iconImages[i].Length); // Image size
            writer.Write(offset);           // Offset
            offset += iconImages[i].Length;
        }

        // Image data
        foreach (var imageData in iconImages)
        {
            writer.Write(imageData);
        }
    }

    private static Bitmap CreateIconBitmap(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Background - gradient from dark teal to darker blue
        var rect = new Rectangle(0, 0, size, size);
        using var bgBrush = new LinearGradientBrush(
            rect,
            DrawColor.FromArgb(255, 26, 82, 118),   // Dark teal
            DrawColor.FromArgb(255, 20, 55, 90),    // Darker blue
            LinearGradientMode.ForwardDiagonal);

        // Draw circular background
        g.FillEllipse(bgBrush, 1, 1, size - 2, size - 2);

        // Draw subtle border
        using var borderPen = new Pen(DrawColor.FromArgb(100, 255, 255, 255), size / 32f);
        g.DrawEllipse(borderPen, 2, 2, size - 4, size - 4);

        // Center point
        float cx = size / 2f;
        float cy = size / 2f;
        float scale = size / 64f;

        // Draw stylized CT/MRI scan lines (concentric arcs)
        using var scanPen = new Pen(DrawColor.FromArgb(180, 200, 230, 255), size / 24f);
        scanPen.StartCap = LineCap.Round;
        scanPen.EndCap = LineCap.Round;

        // Inner scan arc
        float innerRadius = size * 0.25f;
        g.DrawArc(scanPen, cx - innerRadius, cy - innerRadius,
                  innerRadius * 2, innerRadius * 2, -135, 90);

        // Outer scan arc
        float outerRadius = size * 0.35f;
        g.DrawArc(scanPen, cx - outerRadius, cy - outerRadius,
                  outerRadius * 2, outerRadius * 2, 45, 90);

        // Draw small chart bars (representing RVU tracking)
        using var chartBrush = new SolidBrush(DrawColor.FromArgb(220, 100, 200, 150)); // Soft green
        float barWidth = size * 0.06f;
        float barSpacing = size * 0.08f;
        float baseY = cy + size * 0.15f;
        float barX = cx - size * 0.12f;

        // Three ascending bars
        float[] barHeights = { size * 0.12f, size * 0.18f, size * 0.24f };
        for (int i = 0; i < 3; i++)
        {
            var barRect = new RectangleF(
                barX + (i * barSpacing),
                baseY - barHeights[i],
                barWidth,
                barHeights[i]);
            g.FillRectangle(chartBrush, barRect);
        }

        // Draw "RVU" text hint (just a small white dot or accent for smaller sizes)
        if (size >= 48)
        {
            using var accentBrush = new SolidBrush(DrawColor.FromArgb(200, 255, 255, 255));
            float dotSize = size * 0.06f;
            g.FillEllipse(accentBrush, cx + size * 0.2f, cy - size * 0.25f, dotSize, dotSize);
        }

        return bitmap;
    }

    /// <summary>
    /// Ensure the icon file exists, generate if missing.
    /// </summary>
    public static void EnsureIconExists()
    {
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "app.ico");
        var resourcesDir = Path.GetDirectoryName(iconPath);

        if (!Directory.Exists(resourcesDir))
        {
            Directory.CreateDirectory(resourcesDir!);
        }

        if (!File.Exists(iconPath))
        {
            GenerateIcon(iconPath);
        }
    }
}
