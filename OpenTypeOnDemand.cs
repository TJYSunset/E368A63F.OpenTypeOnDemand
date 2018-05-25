using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MoreLinq;
using NLog;
using SharpFont;
using Encoding = System.Text.Encoding;

namespace E368A63F.FreeTypeOnDemand
{
    public static class OpenTypeOnDemand
    {
        public enum WrapMode
        {
            None,
            BreakCharacter,
            BreakWord
        }

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public static readonly Library FreeType = new Library();

        private static readonly Dictionary<(uint index, Face face, uint size, Color color), Texture2D> Cache =
            new Dictionary<(uint index, Face face, uint size, Color color), Texture2D>();

        public static LoadFlags FreeTypeBehavior { get; set; } = LoadFlags.NoAutohint;
        public static LoadTarget FreeTypeTarget { get; set; } = LoadTarget.Light;
        public static RenderMode FreeTypeRenderMode { get; set; } = RenderMode.Light;

        /// <summary>
        ///     Returns the OPPOSITE of whether <paramref name="prev" /> should be placed on a new line.
        /// </summary>
        private static bool IsWordBreak(uint current, uint prev)
        {
            if (WordBreak.BreakAfter.Contains(prev) || WordBreak.BreakBefore.Contains(current)) return true;
            if (WordBreak.NoBreakAfter.Contains(prev) || WordBreak.NoBreakBefore.Contains(current)) return false;
            if (WordBreak.LetterOrDigit.IsMatch(
                    Encoding.UTF32.GetString(BitConverter.GetBytes(Math.Min(prev, char.MaxValue)))) &&
                WordBreak.LetterOrDigit.IsMatch(
                    Encoding.UTF32.GetString(BitConverter.GetBytes(Math.Min(current, char.MaxValue))))) return false;
            return true;
        }

        /// <summary>
        ///     Renders the given runs to <see cref="Texture2D" />s, then return them with their positions.
        /// </summary>
        /// <remarks>
        ///     How this works:
        ///     1. measure glyph metrics using FreeType
        ///     2. wrap
        ///     3. render using FreeType
        /// </remarks>
        public static IEnumerable<(Texture2D texture, Vector2 position, Color color)> Render(
            GraphicsDevice graphicsDevice,
            IEnumerable<(string content, Style style)> runs,
            Rectangle box,
            WrapMode wrapMode)
        {
            #region measure

            FtGlyphInfo MeasureSpecialCharacter(uint index)
            {
                return new FtGlyphInfo(index, null, 0, 0, null, Color.Transparent);
            }

            var glyphs = runs
                .SelectMany(run => Encoding.UTF32.GetBytes(run.content.Replace("\r\n", "\n").Replace('\r', '\n'))
                    .Batch(sizeof(uint) / sizeof(byte))
                    .Select(x => (BitConverter.ToUInt32(x.ToArray(), 0), run.style)))
                .Append<(uint index, Style style)>((0, new Style(new Face[0], 0, 0, Color.Transparent)))
                .Pairwise((x, next) =>
                {
                    switch (x.index)
                    {
                        case '\n':
                            return MeasureSpecialCharacter(x.index);
                        default:
                            // TODO read kern table
                            var face = x.style.FontFaceCandidates
                                .FirstOrDefault(candidate => candidate.GetCharIndex(x.index) != 0);
                            if (face == null)
                            {
                                Logger.Warn(
                                    $"no available font face for character 0x{x.index:X}; candidates are {string.Join(", ", x.style.FontFaceCandidates.Select(f => $"\"{f.FamilyName}\""))}");
                                return MeasureSpecialCharacter(0x00);
                            }

                            face.SetPixelSizes(0, x.style.Size);
                            face.LoadChar(x.index, FreeTypeBehavior, FreeTypeTarget);
                            return new FtGlyphInfo(x.index, face.Glyph.Metrics, x.style.Size,
                                x.style.LineHeight, face,
                                x.style.Color);
                    }
                });

            #endregion

            #region arrange

            var lines = new List<Line>(new[] {new Line()});

            #region wrap

            void NewLine()
            {
                lines.Add(new Line());
            }

            void AppendGlyph(FtGlyphInfo glyph)
            {
                lines.Last().Glyphs.Add(glyph);
                lines.Last().AllocatedWidth += glyph.Metrics.HorizontalAdvance.ToDouble();
            }

            foreach (var glyph in glyphs)
                if (glyph.Face == null)
                {
                    // special character
                    switch (glyph.Index)
                    {
                        case '\n':
                            NewLine();
                            break;
                        default:
                            Logger.Warn($"not implemented special character {glyph.Index:X}");
                            break;
                    }
                }
                else
                {
                    var last = lines.Last();
                    switch (wrapMode)
                    {
                        case WrapMode.None:
                            break;
                        case WrapMode.BreakCharacter:
                            if (glyph.Metrics.Width > 0 &&
                                last.AllocatedWidth + glyph.Metrics.Width.ToDouble() > box.Width)
                                NewLine();
                            break;
                        case WrapMode.BreakWord:
                            if (glyph.Metrics.Width > 0 &&
                                last.AllocatedWidth + glyph.Metrics.Width.ToDouble() > box.Width)
                            {
                                var word = new Stack<FtGlyphInfo>();
                                while (last.Glyphs.Any())
                                {
                                    var lastGlyph = last.Glyphs.Last();
                                    if (IsWordBreak(glyph.Index, lastGlyph.Index)) break;
                                    word.Push(lastGlyph);
                                    last.Glyphs.RemoveAt(last.Glyphs.Count - 1);
                                    last.AllocatedWidth -= lastGlyph.Metrics.HorizontalAdvance.ToDouble();
                                }

                                if (last.Glyphs.Any()) NewLine();

                                lines.Last().Glyphs.AddRange(word);
                            }

                            break;
                        default:
                            Logger.Error($"not implemented wrap mode {wrapMode}, treating as no wrap");
                            break;
                    }

                    AppendGlyph(glyph);
                }

            #endregion

            #region align & render

            double penX = 0, penY = 0;
            foreach (var line in lines.Select(x => x.Glyphs))
            {
                var baseline = line.Max(x => x.LineHeight);

                foreach (var glyph in line)
                {
                    if (glyph.Index > 0x00)
                        lock (Cache)
                        {
                            var cacheIndex = (glyph.Index, glyph.Face, glyph.Size,
                                glyph.Color);
                            var face = glyph.Face;
                            if (Cache.ContainsKey(cacheIndex))
                            {
                                var render = Cache[cacheIndex];
                                yield return (render,
                                    new Vector2((float) (penX + box.X + face.Glyph.BitmapLeft),
                                        (float) (penY + box.Y + baseline - glyph.Metrics.HorizontalBearingY.ToDouble() +
                                                 face.Glyph.BitmapTop)),
                                    glyph.Color);
                            }
                            else
                            {
                                face.SetPixelSizes(0, glyph.Size);
                                face.LoadChar(glyph.Index, FreeTypeBehavior, FreeTypeTarget);
                                face.Glyph.RenderGlyph(FreeTypeRenderMode);
                                var bitmap = face.Glyph.Bitmap;
                                if (bitmap.Width > 0)
                                {
                                    var render = new Texture2D(graphicsDevice,
                                        bitmap.Width,
                                        bitmap.Rows, false,
                                        SurfaceFormat.ColorSRgb);
                                    render.SetData(bitmap.BufferData.SelectMany(x => new[] {x, x, x, x}).ToArray());
                                    Cache[cacheIndex] = render;
                                    yield return (render,
                                        new Vector2((float) (penX + box.X + face.Glyph.BitmapLeft),
                                            (float) (penY + box.Y + baseline -
                                                     glyph.Metrics.HorizontalBearingY.ToDouble() +
                                                     face.Glyph.BitmapTop)),
                                        glyph.Color);
                                    bitmap.Dispose();
                                }
                            }
                        }

                    penX += glyph.Metrics.HorizontalAdvance.ToDouble();
                }

                penX = 0;
                penY += line.Max(x => x.LineHeight);
            }

            #endregion

            #endregion
        }

        // BUG this is not thread safe, despite of using lock
        // will cause NullReferenceException if called in the middle of drawing glyphs
        public static void PurgeCache()
        {
            lock (Cache)
            {
                foreach (var texture in Cache.Values) texture.Dispose();

                Cache.Clear();
            }
        }

        /// <summary>
        ///     Specify a run's style.
        /// </summary>
        public class Style
        {
            public Color Color;
            public IList<Face> FontFaceCandidates;
            public float LineHeight;
            public uint Size;

            public Style(IList<Face> fontFaceCandidates, uint size, float lineHeight, Color color)
            {
                FontFaceCandidates = fontFaceCandidates;
                Size = size;
                LineHeight = lineHeight;
                Color = color;
            }
        }

        /// <summary>
        ///     "FreeType glyph info"
        ///     Rough metrics for measuring and wrapping.
        /// </summary>
        private class FtGlyphInfo
        {
            public FtGlyphInfo(uint index, GlyphMetrics metrics, uint size, float lineHeight, Face face,
                Color color)
            {
                Index = index;
                Metrics = metrics;
                Size = size;
                LineHeight = lineHeight;
                Face = face;
                Color = color;
            }

            public uint Index { get; }
            public GlyphMetrics Metrics { get; }
            public uint Size { get; }
            public float LineHeight { get; }
            public Face Face { get; }
            public Color Color { get; }
        }

        /// <summary>
        ///     They say you can't change the property of a byval object like <see cref="ValueTuple{T1,T2}" />.
        /// </summary>
        private class Line
        {
            public List<FtGlyphInfo> Glyphs { get; } = new List<FtGlyphInfo>();
            public double AllocatedWidth { get; set; }
        }
    }
}