using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;
using AudioBit.Core;
using QRCoder;

namespace AudioBit.App.Services;

internal sealed class QrCodeService
{
    private const int ModuleSize = 12;
    private const int QuietZoneModules = 1;
    private const float ModuleInsetRatio = 0.075f;
    private const float ModuleCornerRatio = 0.24f;
    private const float CenterLogoBadgeRatio = 0.165f;
    private const float CenterLogoCornerRatio = 0.26f;
    private const float CenterLogoBorderRatio = 0.03f;
    private const float CenterLogoWaveStrokeRatio = 0.18f;
    private static readonly Color QrModuleColor = Color.FromArgb(255, 255, 132, 26);
    private static readonly Color QrBackgroundColor = Color.FromArgb(255, 14, 14, 14);
    private static readonly Color CenterLogoTopColor = Color.FromArgb(255, 255, 146, 39);
    private static readonly Color CenterLogoBottomColor = Color.FromArgb(255, 255, 122, 16);
    private static readonly Color CenterLogoBorderColor = Color.FromArgb(255, 182, 85, 18);
    private static readonly Color CenterLogoWaveColor = Color.FromArgb(250, 250, 250);
    private readonly Uri _remoteConnectBaseUri;

    public QrCodeService(ExternalLinksConfiguration externalLinks)
    {
        ArgumentNullException.ThrowIfNull(externalLinks);
        _remoteConnectBaseUri = externalLinks.RemoteConnectBaseUri;
    }

    public string BuildPairUrl(string? sid, string? pairCode)
    {
        if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(pairCode))
        {
            return string.Empty;
        }

        var existingQuery = string.Empty;
        var builder = new UriBuilder(_remoteConnectBaseUri);
        if (!string.IsNullOrWhiteSpace(builder.Query))
        {
            existingQuery = builder.Query.TrimStart('?') + "&";
        }

        builder.Query = $"{existingQuery}sid={Uri.EscapeDataString(sid)}&code={Uri.EscapeDataString(pairCode)}";
        return builder.Uri.AbsoluteUri;
    }

    public BitmapSource? GeneratePairQr(string sid, string pairCode)
    {
        var pairUrl = BuildPairUrl(sid, pairCode);
        if (string.IsNullOrWhiteSpace(pairUrl))
        {
            return null;
        }

        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(pairUrl, QRCodeGenerator.ECCLevel.H);
        using var bitmap = RenderStyledQr(qrData);
        return ToBitmapSource(bitmap);
    }

    private static Bitmap RenderStyledQr(QRCodeData qrData)
    {
        var moduleCount = qrData.ModuleMatrix.Count;
        var canvasSize = (moduleCount + (QuietZoneModules * 2)) * ModuleSize;
        var moduleInset = Math.Max(0.75f, ModuleSize * ModuleInsetRatio);

        var bitmap = new Bitmap(canvasSize, canvasSize, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(QrBackgroundColor);

        using var moduleBrush = new SolidBrush(QrModuleColor);
        for (var y = 0; y < moduleCount; y++)
        {
            var row = qrData.ModuleMatrix[y];
            for (var x = 0; x < moduleCount; x++)
            {
                if (!row[x])
                {
                    continue;
                }

                var left = (x + QuietZoneModules) * ModuleSize;
                var top = (y + QuietZoneModules) * ModuleSize;
                var roundedModuleRect = new RectangleF(
                    left + moduleInset,
                    top + moduleInset,
                    ModuleSize - (moduleInset * 2f),
                    ModuleSize - (moduleInset * 2f));
                var moduleRadius = roundedModuleRect.Width * ModuleCornerRatio;
                using var modulePath = CreateRoundedRectPath(roundedModuleRect, moduleRadius);
                graphics.FillPath(moduleBrush, modulePath);
            }
        }

        DrawCenterLogo(graphics, canvasSize);

        return bitmap;
    }

    private static void DrawCenterLogo(Graphics graphics, float canvasSize)
    {
        var badgeSize = canvasSize * CenterLogoBadgeRatio;
        var badgeLeft = (canvasSize - badgeSize) * 0.5f;
        var badgeTop = (canvasSize - badgeSize) * 0.5f;
        var badgeRadius = badgeSize * CenterLogoCornerRatio;
        var borderSize = Math.Max(1f, badgeSize * CenterLogoBorderRatio);

        var waveWidth = badgeSize * 0.66f;
        var waveHeight = waveWidth * 0.36f;
        var waveStroke = Math.Max(1.8f, waveWidth * CenterLogoWaveStrokeRatio);
        var waveLeft = badgeLeft + ((badgeSize - waveWidth) * 0.5f);
        var waveTop = badgeTop + ((badgeSize - waveHeight) * 0.5f);

        var previousSmoothing = graphics.SmoothingMode;
        var previousPixelOffset = graphics.PixelOffsetMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var badgePath = CreateRoundedRectPath(new RectangleF(badgeLeft, badgeTop, badgeSize, badgeSize), badgeRadius);
        using var badgeBrush = new LinearGradientBrush(
            new PointF(badgeLeft, badgeTop),
            new PointF(badgeLeft, badgeTop + badgeSize),
            CenterLogoTopColor,
            CenterLogoBottomColor);
        using var badgeBorderPen = new Pen(CenterLogoBorderColor, borderSize)
        {
            LineJoin = LineJoin.Round,
        };
        graphics.FillPath(badgeBrush, badgePath);
        graphics.DrawPath(badgeBorderPen, badgePath);

        using var wavePath = new GraphicsPath();
        wavePath.StartFigure();
        wavePath.AddLine(
            waveLeft,
            waveTop + (waveHeight * 0.60f),
            waveLeft + (waveWidth * 0.24f),
            waveTop + (waveHeight * 0.60f));
        wavePath.AddBezier(
            waveLeft + (waveWidth * 0.24f),
            waveTop + (waveHeight * 0.60f),
            waveLeft + (waveWidth * 0.36f),
            waveTop + (waveHeight * 0.60f),
            waveLeft + (waveWidth * 0.34f),
            waveTop + (waveHeight * 0.14f),
            waveLeft + (waveWidth * 0.46f),
            waveTop + (waveHeight * 0.14f));
        wavePath.AddBezier(
            waveLeft + (waveWidth * 0.46f),
            waveTop + (waveHeight * 0.14f),
            waveLeft + (waveWidth * 0.57f),
            waveTop + (waveHeight * 0.14f),
            waveLeft + (waveWidth * 0.53f),
            waveTop + (waveHeight * 0.86f),
            waveLeft + (waveWidth * 0.66f),
            waveTop + (waveHeight * 0.86f));
        wavePath.AddBezier(
            waveLeft + (waveWidth * 0.66f),
            waveTop + (waveHeight * 0.86f),
            waveLeft + (waveWidth * 0.76f),
            waveTop + (waveHeight * 0.86f),
            waveLeft + (waveWidth * 0.74f),
            waveTop + (waveHeight * 0.40f),
            waveLeft + (waveWidth * 0.86f),
            waveTop + (waveHeight * 0.40f));
        wavePath.AddBezier(
            waveLeft + (waveWidth * 0.86f),
            waveTop + (waveHeight * 0.40f),
            waveLeft + (waveWidth * 0.94f),
            waveTop + (waveHeight * 0.40f),
            waveLeft + (waveWidth * 0.94f),
            waveTop + (waveHeight * 0.60f),
            waveLeft + waveWidth,
            waveTop + (waveHeight * 0.60f));

        using var wavePen = new Pen(CenterLogoWaveColor, waveStroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        graphics.DrawPath(wavePen, wavePath);
        graphics.SmoothingMode = previousSmoothing;
        graphics.PixelOffsetMode = previousPixelOffset;
    }

    private static GraphicsPath CreateRoundedRectPath(RectangleF bounds, float radius)
    {
        var cappedRadius = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f);
        var diameter = cappedRadius * 2f;

        var path = new GraphicsPath();
        if (diameter <= 0.01f)
        {
            path.AddRectangle(bounds);
            path.CloseFigure();
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

}
