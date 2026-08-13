using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Bypass_OOBE.Services;

// Minimal renderer for simple SVG path data (M/L/C/A/Z only - the subset the eye icons use).
// Avoids pulling in a full SVG dependency for two small glyphs.
internal static partial class VectorIcon
{
    private const float ViewBoxSize = 24f;

    public static Bitmap Render(string pathData, int size, Color color)
    {
        using var path = ParsePath(pathData);
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.ScaleTransform(size / ViewBoxSize, size / ViewBoxSize);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
        return bitmap;
    }

    [GeneratedRegex(@"([MmLlCcAaZz])([^MmLlCcAaZz]*)")]
    private static partial Regex CommandRegex();

    [GeneratedRegex(@"-?\d*\.?\d+")]
    private static partial Regex NumberRegex();

    private static GraphicsPath ParsePath(string d)
    {
        var path = new GraphicsPath();
        float currentX = 0, currentY = 0, startX = 0, startY = 0;

        foreach (Match commandMatch in CommandRegex().Matches(d))
        {
            var command = commandMatch.Groups[1].Value[0];
            var isRelative = char.IsLower(command);
            var numbers = NumberRegex().Matches(commandMatch.Groups[2].Value)
                .Select(m => float.Parse(m.Value, CultureInfo.InvariantCulture))
                .ToArray();

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    for (var i = 0; i < numbers.Length; i += 2)
                    {
                        var x = isRelative ? currentX + numbers[i] : numbers[i];
                        var y = isRelative ? currentY + numbers[i + 1] : numbers[i + 1];
                        if (i == 0)
                        {
                            path.StartFigure();
                            startX = x;
                            startY = y;
                        }
                        else
                        {
                            path.AddLine(currentX, currentY, x, y);
                        }

                        currentX = x;
                        currentY = y;
                    }

                    break;

                case 'L':
                    for (var i = 0; i < numbers.Length; i += 2)
                    {
                        var x = isRelative ? currentX + numbers[i] : numbers[i];
                        var y = isRelative ? currentY + numbers[i + 1] : numbers[i + 1];
                        path.AddLine(currentX, currentY, x, y);
                        currentX = x;
                        currentY = y;
                    }

                    break;

                case 'C':
                    for (var i = 0; i < numbers.Length; i += 6)
                    {
                        var c1X = isRelative ? currentX + numbers[i] : numbers[i];
                        var c1Y = isRelative ? currentY + numbers[i + 1] : numbers[i + 1];
                        var c2X = isRelative ? currentX + numbers[i + 2] : numbers[i + 2];
                        var c2Y = isRelative ? currentY + numbers[i + 3] : numbers[i + 3];
                        var x = isRelative ? currentX + numbers[i + 4] : numbers[i + 4];
                        var y = isRelative ? currentY + numbers[i + 5] : numbers[i + 5];
                        path.AddBezier(currentX, currentY, c1X, c1Y, c2X, c2Y, x, y);
                        currentX = x;
                        currentY = y;
                    }

                    break;

                case 'A':
                    for (var i = 0; i < numbers.Length; i += 7)
                    {
                        var rx = numbers[i];
                        var ry = numbers[i + 1];
                        // numbers[i + 2] is the x-axis rotation; both source icons only ever use 0, so it's ignored here.
                        var largeArc = numbers[i + 3] != 0;
                        var sweep = numbers[i + 4] != 0;
                        var x = isRelative ? currentX + numbers[i + 5] : numbers[i + 5];
                        var y = isRelative ? currentY + numbers[i + 6] : numbers[i + 6];
                        AddArc(path, currentX, currentY, x, y, rx, ry, largeArc, sweep);
                        currentX = x;
                        currentY = y;
                    }

                    break;

                case 'Z':
                    path.CloseFigure();
                    currentX = startX;
                    currentY = startY;
                    break;
            }
        }

        return path;
    }

    // SVG endpoint-to-center arc parameterization (spec F.6.5), simplified for x-axis-rotation = 0.
    private static void AddArc(GraphicsPath path, float x1, float y1, float x2, float y2, float rx, float ry, bool largeArc, bool sweep)
    {
        var dx2 = (x1 - x2) / 2f;
        var dy2 = (y1 - y2) / 2f;

        var lambda = (dx2 * dx2 / (rx * rx)) + (dy2 * dy2 / (ry * ry));
        if (lambda > 1)
        {
            var scale = MathF.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }

        var sign = largeArc != sweep ? 1f : -1f;
        var num = (rx * rx * ry * ry) - (rx * rx * dy2 * dy2) - (ry * ry * dx2 * dx2);
        var den = (rx * rx * dy2 * dy2) + (ry * ry * dx2 * dx2);
        var co = den == 0 ? 0 : sign * MathF.Sqrt(Math.Max(0f, num / den));

        var cx1 = co * rx * dy2 / ry;
        var cy1 = co * -ry * dx2 / rx;

        var cx = cx1 + ((x1 + x2) / 2f);
        var cy = cy1 + ((y1 + y2) / 2f);

        var theta1 = SignedAngleDegrees(1, 0, (dx2 - cx1) / rx, (dy2 - cy1) / ry);
        var deltaTheta = SignedAngleDegrees((dx2 - cx1) / rx, (dy2 - cy1) / ry, (-dx2 - cx1) / rx, (-dy2 - cy1) / ry);

        if (!sweep && deltaTheta > 0)
        {
            deltaTheta -= 360;
        }
        else if (sweep && deltaTheta < 0)
        {
            deltaTheta += 360;
        }

        path.AddArc(cx - rx, cy - ry, rx * 2, ry * 2, theta1, deltaTheta);
    }

    private static float SignedAngleDegrees(float ux, float uy, float vx, float vy)
    {
        var angle = MathF.Atan2((ux * vy) - (uy * vx), (ux * vx) + (uy * vy));
        return angle * (180f / MathF.PI);
    }
}
