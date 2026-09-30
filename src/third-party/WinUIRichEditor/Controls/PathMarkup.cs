using System;
using System.Globalization;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace WinUIRichEditor.Controls;

// Parses the path mini-language the toolbar's vector icons are written in — M L H V Q A Z, absolute and
// relative, implicit repeats — into a PathGeometry. WinUI has no public string→Geometry parse for code
// (Avalonia's Geometry.Parse); the XAML routes (XamlReader.Load, XamlBindingHelper.ConvertValue) resolve
// types at run time, which trimming and Native AOT can break silently — this has no such dependency and is
// small enough to own. Commands the icons do not use (C S T) are rejected rather than guessed.
internal static class PathMarkup
{
    public static PathGeometry Parse(string data)
    {
        var geometry = new PathGeometry();
        PathFigure? figure = null;
        Point current = default, start = default;
        char command = '\0';
        int i = 0;

        void Add(PathSegment segment)
        {
            // A drawing command after Z (or first) opens a figure at the current point, as in SVG.
            if (figure == null)
            {
                figure = new PathFigure { StartPoint = current, IsFilled = true };
                geometry.Figures.Add(figure);
            }
            figure.Segments.Add(segment);
        }

        while (true)
        {
            SkipSeparators(data, ref i);
            if (i >= data.Length) break;
            if (char.IsLetter(data[i])) command = data[i++];
            else if (command == '\0' || char.ToUpperInvariant(command) == 'Z')
                throw new FormatException($"Path data: number without a command at {i} in \"{data}\".");

            bool relative = char.IsLower(command);
            Point origin = relative ? current : default;
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    current = start = Offset(origin, Number(data, ref i), Number(data, ref i));
                    figure = new PathFigure { StartPoint = current, IsFilled = true };
                    geometry.Figures.Add(figure);
                    command = relative ? 'l' : 'L'; // further coordinate pairs are line-tos
                    break;
                case 'L':
                    current = Offset(origin, Number(data, ref i), Number(data, ref i));
                    Add(new LineSegment { Point = current });
                    break;
                case 'H':
                    current = new Point(Number(data, ref i) + origin.X, current.Y);
                    Add(new LineSegment { Point = current });
                    break;
                case 'V':
                    current = new Point(current.X, Number(data, ref i) + origin.Y);
                    Add(new LineSegment { Point = current });
                    break;
                case 'Q':
                {
                    var control = Offset(origin, Number(data, ref i), Number(data, ref i));
                    current = Offset(origin, Number(data, ref i), Number(data, ref i));
                    Add(new QuadraticBezierSegment { Point1 = control, Point2 = current });
                    break;
                }
                case 'A':
                {
                    double rx = Number(data, ref i), ry = Number(data, ref i), rotation = Number(data, ref i);
                    bool large = Number(data, ref i) != 0, sweep = Number(data, ref i) != 0;
                    current = Offset(origin, Number(data, ref i), Number(data, ref i));
                    Add(new ArcSegment
                    {
                        Point = current, Size = new Size(rx, ry), RotationAngle = rotation, IsLargeArc = large,
                        // y grows downward, so SVG's positive-angle sweep is clockwise on screen.
                        SweepDirection = sweep ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                    });
                    break;
                }
                case 'Z':
                    if (figure != null) figure.IsClosed = true;
                    figure = null;
                    current = start;
                    break;
                default:
                    throw new FormatException($"Path data: unsupported command '{command}' in \"{data}\".");
            }
        }
        return geometry;
    }

    private static Point Offset(Point origin, double x, double y) => new(origin.X + x, origin.Y + y);

    private static void SkipSeparators(string s, ref int i)
    {
        while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
    }

    private static double Number(string s, ref int i)
    {
        SkipSeparators(s, ref i);
        int begin = i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
        }
        if (!double.TryParse(s.AsSpan(begin, i - begin), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            throw new FormatException($"Path data: expected a number at {begin} in \"{s}\".");
        return v;
    }
}
