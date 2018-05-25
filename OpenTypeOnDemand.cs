using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MoreLinq;
using NLog;
using SharpFont;
using SharpFont.HarfBuzz;
using Buffer = SharpFont.HarfBuzz.Buffer;
using Encoding = System.Text.Encoding;

namespace E368A63F.FreeTypeOnDemand
{
    public static class OpenTypeOnDemand
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public static readonly Library FreeType = new Library();
        public static LoadFlags FreeTypeBehavior { get; set; } = LoadFlags.NoAutohint;
        public static LoadTarget FreeTypeTarget { get; set; } = LoadTarget.Light;
        public static RenderMode FreeTypeRenderMode { get; set; } = RenderMode.Light;

        private static readonly Dictionary<(uint index, Face face, uint size, Color color), Texture2D> Cache =
            new Dictionary<(uint index, Face face, uint size, Color color), Texture2D>();

        private static readonly Dictionary<Face, Font> HarfBuzzCache = new Dictionary<Face, Font>();

        /// <summary>
        /// Specify a run's style.
        /// </summary>
        public class Style
        {
            public IList<Face> FontFaceCandidates;
            public uint Size;
            public float LineHeight;
            public Color Color;

            public Style(IList<Face> fontFaceCandidates, uint size, float lineHeight, Color color)
            {
                FontFaceCandidates = fontFaceCandidates;
                Size = size;
                LineHeight = lineHeight;
                Color = color;
            }
        }

        public enum WrapMode
        {
            None,
            BreakCharacter,
            BreakWord
        }

        /// <summary>
        /// "FreeType glyph info"
        /// Rough metrics for measuring and wrapping, but not for rendering.
        /// </summary>
        private class FtGlyphInfo
        {
            public uint Index { get; set; }
            public GlyphMetrics Metrics { get; set; }
            public uint Size { get; set; }
            public float LineHeight { get; set; }
            public Face Face { get; set; }
            public Color Color { get; set; }

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
        }

        /// <summary>
        /// They say you can't change the property of a byval object like <see cref="ValueTuple{T,T}"/>.
        /// </summary>
        private class Line
        {
            public List<FtGlyphInfo> Glyphs { get; set; } = new List<FtGlyphInfo>();
            public double AllocatedWidth { get; set; }
        }

        /// <summary>
        /// Returns the OPPOSITE of whether <paramref name="prev"/> should be placed on a new line.
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
        /// Renders the given runs to <see cref="Texture2D"/>s, then return them with their positions.
        /// </summary>
        /// <remarks>
        /// How this works:
        /// 1. measure glyph metrics using FreeType
        /// 2. wrap
        /// 3. render using FreeType, but use metrics from HarfBuzz
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

            // BUG missing the characters where two runs meet
            var glyphs = runs.SelectMany(run =>
                Encoding.UTF32.GetBytes(run.content.Replace("\r\n", "\n").Replace('\r', '\n') + '\u0000')
                    .Batch(sizeof(uint) / sizeof(byte))
                    .Select(x => BitConverter.ToUInt32(x.ToArray(), 0))
                    .Pairwise((x, next) =>
                    {
                        switch (x)
                        {
                            case '\n':
                                return MeasureSpecialCharacter(x);
                            default:
                                var face = run.style.FontFaceCandidates
                                    .FirstOrDefault(candidate => candidate.GetCharIndex(x) != 0);
                                if (face == null)
                                {
                                    Logger.Warn(
                                        $"no available font face for character 0x{x:X}; candidates are {string.Join(", ", run.style.FontFaceCandidates.Select(f => $"\"{f.FamilyName}\""))}");
                                    return MeasureSpecialCharacter(0x00);
                                }

                                face.SetPixelSizes(0, run.style.Size);
                                face.LoadChar(x, FreeTypeBehavior, FreeTypeTarget);
                                return new FtGlyphInfo(x, face.Glyph.Metrics, run.style.Size,
                                    run.style.LineHeight, face,
                                    run.style.Color);
                        }
                    }));

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
            {
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
                            if (last.AllocatedWidth + glyph.Metrics.Width.ToDouble() > box.Width)
                                NewLine();
                            break;
                        case WrapMode.BreakWord:
                            if (last.AllocatedWidth + glyph.Metrics.Width.ToDouble() > box.Width)
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

                                if (last.Glyphs.Any())
                                {
                                    NewLine();
                                }

                                lines.Last().Glyphs.AddRange(word);
                            }

                            break;
                        default:
                            Logger.Error($"not implemented wrap mode {wrapMode}, treating as no wrap");
                            break;
                    }

                    AppendGlyph(glyph);
                }
            }

            #endregion

            #region align & render

            double penX = 0, penY = 0;
            foreach (var line in lines.Select(x => x.Glyphs))
            {
                var baseline = line.Max(x => x.LineHeight);
                var subruns = line.GroupAdjacent(x => (x.Face, x.Size)).Select(subrun =>
                {
                    var face = subrun.Key.Item1;
                    Font font;
                    if (HarfBuzzCache.ContainsKey(face))
                    {
                        font = HarfBuzzCache[face];
                    }
                    else
                    {
                        font = Font.FromFTFace(face);
                        HarfBuzzCache[face] = font;
                    }

                    var buffer = new Buffer();
                    buffer.AddText(
                        string.Concat(subrun.Select(x => Encoding.UTF32.GetString(BitConverter.GetBytes(x.Index)))));
//                    buffer.GuessSegmentProperties();
                    font.Shape(buffer);
                    return subrun.EquiZip(buffer.GlyphInfo(), buffer.GlyphPositions(),
                        (x, y, z) => ((FtGlyphInfo info, GlyphInfo hbInfo, GlyphPosition hbPosition)) (x, y, z));
                });

                double offsetX = 0; // aggregated width of previous subruns
                foreach (var subrun in subruns)
                {
                    foreach (var glyph in subrun)
                    {
                        if (glyph.info.Index > 0x00)
                        {
                            lock (Cache)
                            {
                                // TODO deal with that fucking harfbuzz here

                                var cacheIndex = (glyph.hbInfo.codepoint, glyph.info.Face, glyph.info.Size,
                                    glyph.info.Color);
                                var face = glyph.info.Face;
                                if (Cache.ContainsKey(cacheIndex))
                                {
                                    var render = Cache[cacheIndex];
                                    yield return (render,
                                        new Vector2((float) (offsetX + penX + box.X + face.Glyph.BitmapLeft),
                                            (float) (penY + box.Y + baseline - face.Glyph.BitmapTop)),
                                        glyph.info.Color);
                                }
                                else
                                {
                                    face.SetPixelSizes(0, glyph.info.Size);
                                    face.LoadChar(glyph.hbInfo.codepoint, FreeTypeBehavior, FreeTypeTarget);
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
                                            new Vector2((float) (offsetX + penX + box.X + face.Glyph.BitmapLeft),
                                                (float) (penY + box.Y + baseline - face.Glyph.BitmapTop)),
                                            glyph.info.Color);
                                        bitmap.Dispose();
                                    }
                                }
                            }
                        }

                        penX += glyph.hbPosition.xAdvance;
                    }

                    offsetX = penX;
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
                foreach (var texture in Cache.Values)
                {
                    texture.Dispose();
                }

                Cache.Clear();
            }
        }

        #region reflection hacks

//        private static readonly MethodInfo HbBufferGuessSegmentProperties =
//            typeof(HB).GetMethod(@"hb_buffer_guess_segment_properties", BindingFlags.Static | BindingFlags.NonPublic);
//
//        private static void GuessSegmentProperties(this Buffer buffer)
//        {
//            HbBufferGuessSegmentProperties.Invoke(null,
//                new[]
//                {
//                    typeof(Buffer).GetField(@"reference", BindingFlags.Instance | BindingFlags.NonPublic)
//                        .GetValue(buffer)
//                });
//        }

        #endregion
    }
}