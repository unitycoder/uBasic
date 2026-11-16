using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Unitycoder.UBasic {

    /// <summary>How GET and PUT pack pixels into an array. These are the real
    /// QBasic layouts, byte for byte, because classic programs both size
    /// their arrays for them and ship sprites as DATA in them.</summary>
    public enum ImageFormat {
        Byte8,      // SCREEN 13 and custom sizes: one byte per pixel
        Planar4,    // SCREEN 7, 8, 9, 12: four bit planes per row
        Packed2,    // SCREEN 1: two bits per pixel
        Packed1     // SCREEN 2, 11: one bit per pixel
    }

    /// <summary>The uBasic "screen": a palette-indexed framebuffer plus a text
    /// cursor. Deliberately engine-free -- Unity only ever reads Pix and Palette
    /// to fill a Texture2D, so the whole language can be tested headlessly.
    ///
    /// Text is drawn into the same framebuffer as graphics, including in
    /// SCREEN 0, so there is one code path and one thing to upload.</summary>
    public class Screen {
        public const int CharW = Font6x8.CellW;
        public const int CharH = Font6x8.CellH;
        public const int MaxDim = 2048;
        /// <summary>Mode value of a free-size uBasic screen (SCREEN w, h).</summary>
        public const int CustomMode = -1;

        public int W { get; private set; }
        public int H { get; private set; }
        public byte[] Pix { get; private set; }     // one palette index per pixel
        public readonly byte[] Palette = new byte[256 * 3];
        public bool Dirty = true;

        public int Fg = 15, Bg = 0;
        public int CurRow, CurCol;                  // 0-based

        /// <summary>QBasic SCREEN mode number, or CustomMode.</summary>
        public int Mode { get; private set; }
        public int CellW { get; private set; }
        public int CellH { get; private set; }
        /// <summary>Highest colour attribute of the mode, as a bit mask.</summary>
        public int ColorMask { get; private set; }
        public ImageFormat ImageFormat { get; private set; }
        /// <summary>Width / height the picture is meant to be shown at. QBasic
        /// modes were all displayed on a 4:3 monitor whatever their pixel
        /// count, so 640x350 has tall pixels.</summary>
        public float DisplayAspect { get; private set; }
        /// <summary>Text viewport (VIEW PRINT), 0-based inclusive rows.</summary>
        public int ViewTop { get; private set; }
        public int ViewBottom { get; private set; }
        /// <summary>Last point referenced by a graphics statement, for STEP
        /// and for LINE -(x, y).</summary>
        public int LastX, LastY;

        public int Cols { get { return W / CellW; } }
        public int Rows { get { return H / CellH; } }
        public bool IsText { get { return Mode == 0; } }

        private static readonly int[] DefaultPalette = {
            0x000000, 0x0000AA, 0x00AA00, 0x00AAAA, 0xAA0000, 0xAA00AA, 0xAA5500, 0xAAAAAA,
            0x555555, 0x5555FF, 0x55FF55, 0x55FFFF, 0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF
        };

        public Screen(int w, int h) {
            Mode = CustomMode;
            CellW = CharW; CellH = CharH;
            ColorMask = 15;
            ResetPalette();
            Resize(w, h);
        }

        // ---- modes ---------------------------------------------------------

        /// <summary>Free-size screen with the compact 6x8 font: the uBasic
        /// dialect's SCREEN w, h.</summary>
        public void Resize(int w, int h) {
            w = Clamp(w, CharW, MaxDim);
            h = Clamp(h, CharH, MaxDim);
            if (Mode == CustomMode && w == W && h == H && Pix != null) return;
            Mode = CustomMode;
            CellW = CharW; CellH = CharH;
            ColorMask = 15;
            ImageFormat = ImageFormat.Byte8;
            Allocate(w, h, (float)w / h);
            ResetPalette();
            Fg = 15; Bg = 0;
            ViewTop = 0; ViewBottom = Math.Max(0, Rows - 1);
        }

        /// <summary>Switch to a QBasic SCREEN mode. Returns false for a mode
        /// number that is not supported.</summary>
        public bool SetMode(int mode) {
            int w, h, ch, mask, fg;
            ImageFormat fmt = ImageFormat.Planar4;
            switch (mode) {
                case 0:  w = 640; h = 400; ch = 16; mask = 15;  fg = 7;  fmt = ImageFormat.Byte8; break;
                case 1:  w = 320; h = 200; ch = 8;  mask = 3;   fg = 3;  fmt = ImageFormat.Packed2; break;
                case 2:  w = 640; h = 200; ch = 8;  mask = 1;   fg = 1;  fmt = ImageFormat.Packed1; break;
                case 7:  w = 320; h = 200; ch = 8;  mask = 15;  fg = 15; break;
                case 8:  w = 640; h = 200; ch = 8;  mask = 15;  fg = 15; break;
                case 9:  w = 640; h = 350; ch = 14; mask = 15;  fg = 15; break;
                case 11: w = 640; h = 480; ch = 16; mask = 1;   fg = 1;  fmt = ImageFormat.Packed1; break;
                case 12: w = 640; h = 480; ch = 16; mask = 15;  fg = 15; break;
                case 13: w = 320; h = 200; ch = 8;  mask = 255; fg = 15; fmt = ImageFormat.Byte8; break;
                default: return false;
            }
            Mode = mode;
            CellW = FontCP437.CellW; CellH = ch;
            ColorMask = mask;
            ImageFormat = fmt;
            Allocate(w, h, 4f / 3f);
            ResetPalette();
            Fg = fg; Bg = 0;
            ResetView();
            return true;
        }

        /// <summary>WIDTH cols, rows. Only meaningful in SCREEN 0, where it
        /// picks between 40/80 columns and 25/43/50 lines.</summary>
        public void SetTextSize(int cols, int rows) {
            if (Mode != 0) return;
            if (cols <= 0) cols = Cols;
            if (rows <= 0) rows = Rows;
            cols = cols <= 40 ? 40 : 80;
            int ch, h;
            if (rows >= 50) { ch = 8; h = 400; }
            else if (rows >= 43) { ch = 8; h = 344; }
            else { ch = 16; h = 400; }
            if (cols * 8 == W && h == H && ch == CellH) return;
            CellH = ch;
            Allocate(cols * 8, h, 4f / 3f);
            ResetView();
        }

        private void Allocate(int w, int h, float aspect) {
            W = w; H = h;
            Pix = new byte[W * H];
            DisplayAspect = aspect;
            CurRow = CurCol = 0;
            LastX = W / 2; LastY = H / 2;
            Dirty = true;
        }

        private void ResetView() {
            // QBasic keeps the bottom line out of the scrolling region: you
            // can LOCATE to it and print there without the screen scrolling.
            ViewTop = 0;
            ViewBottom = Math.Max(0, Rows - 2);
        }

        /// <summary>VIEW PRINT top TO bottom, 0-based inclusive. Pass a
        /// negative top to reset to the whole screen.</summary>
        public void SetTextView(int top, int bottom) {
            if (top < 0) {
                ViewTop = 0;
                ViewBottom = Math.Max(0, Mode == CustomMode ? Rows - 1 : Rows - 2);
            } else {
                ViewTop = Clamp(top, 0, Rows - 1);
                ViewBottom = Clamp(bottom, ViewTop, Rows - 1);
            }
            CurRow = ViewTop; CurCol = 0;
        }

        /// <summary>Default CIRCLE aspect: the y/x radius ratio that makes a
        /// circle look round once the mode is stretched to 4:3.</summary>
        public double CircleAspect {
            get { return Mode == CustomMode ? 1.0 : 4.0 * ((double)H / W) / 3.0; }
        }

        // ---- palette -------------------------------------------------------

        private void SetRgb(int i, int r, int g, int b) {
            Palette[i * 3 + 0] = (byte)r;
            Palette[i * 3 + 1] = (byte)g;
            Palette[i * 3 + 2] = (byte)b;
        }

        public void ResetPalette() {
            for (int i = 0; i < 256; i++) SetRgb(i, 0, 0, 0);
            for (int i = 0; i < 16; i++) {
                int c = DefaultPalette[i];
                SetRgb(i, (c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF);
            }
            if (Mode == 1) {                      // CGA palette 1
                SetRgb(1, 0x55, 0xFF, 0xFF); SetRgb(2, 0xFF, 0x55, 0xFF); SetRgb(3, 0xFF, 0xFF, 0xFF);
            } else if (Mode == 2 || Mode == 11) {
                SetRgb(1, 0xFF, 0xFF, 0xFF);
            } else if (Mode == 13) {
                BuildVga256();
            }
            Dirty = true;
        }

        /// <summary>The standard VGA 256-colour palette: 16 EGA colours, 16
        /// greys, then three brightness bands of 72 hues.</summary>
        private void BuildVga256() {
            int[] grey = { 0, 5, 8, 11, 14, 17, 20, 24, 28, 32, 36, 40, 45, 50, 56, 63 };
            for (int i = 0; i < 16; i++) Set6(16 + i, grey[i], grey[i], grey[i]);
            int[] hi = { 63, 28, 16 };
            int[][] lo = { new[] { 0, 31, 45 }, new[] { 0, 14, 20 }, new[] { 0, 8, 11 } };
            int idx = 32;
            for (int band = 0; band < 3; band++)
                for (int sat = 0; sat < 3; sat++) {
                    int h = hi[band], l = lo[band][sat];
                    for (int hue = 0; hue < 24; hue++) {
                        int seg = hue / 4, k = hue % 4;
                        int up = l + (h - l) * k / 4, dn = h - (h - l) * k / 4;
                        int r, g, b;
                        switch (seg) {
                            case 0: r = up; g = l;  b = h;  break;   // blue -> magenta
                            case 1: r = h;  g = l;  b = dn; break;   // magenta -> red
                            case 2: r = h;  g = up; b = l;  break;   // red -> yellow
                            case 3: r = dn; g = h;  b = l;  break;   // yellow -> green
                            case 4: r = l;  g = h;  b = up; break;   // green -> cyan
                            default: r = l; g = dn; b = h;  break;   // cyan -> blue
                        }
                        Set6(idx++, r, g, b);
                    }
                }
        }

        private void Set6(int i, int r, int g, int b) {
            SetRgb(i, r * 255 / 63, g * 255 / 63, b * 255 / 63);
        }

        /// <summary>uBasic's PALETTE index, r, g, b (0..255 each).</summary>
        public void SetPalette(int i, int r, int g, int b) {
            if (i < 0 || i > 255) return;
            SetRgb(i, Clamp(r, 0, 255), Clamp(g, 0, 255), Clamp(b, 0, 255));
            Dirty = true;
        }

        /// <summary>QBasic's PALETTE attribute, colour. What "colour" means
        /// depends on the mode: an EGA colour number 0..63 in SCREEN 9, a
        /// packed 18-bit VGA value in SCREEN 12 and 13, and one of the 16
        /// standard colours everywhere else.</summary>
        public void SetPaletteQB(int attr, int color) {
            if (attr < 0 || attr > ColorMask) return;
            if (Mode == 12 || Mode == 13) {
                Set6(attr, color & 63, (color >> 8) & 63, (color >> 16) & 63);
            } else if (Mode == 9) {
                int c = color & 63;
                SetRgb(attr,
                    0xAA * ((c >> 2) & 1) + 0x55 * ((c >> 5) & 1),
                    0xAA * ((c >> 1) & 1) + 0x55 * ((c >> 4) & 1),
                    0xAA * (c & 1)        + 0x55 * ((c >> 3) & 1));
            } else {
                int c = DefaultPalette[color & 15];
                SetRgb(attr, (c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF);
            }
            Dirty = true;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        // ---- pixels -------------------------------------------------------

        /// <summary>uBasic's CLS colour: fill everything and make that the
        /// text background.</summary>
        public void Cls(int color) {
            byte c = (byte)(color & ColorMask);
            // Array.Fill is not available on older targets Unity may compile against.
            for (int i = 0; i < Pix.Length; i++) Pix[i] = c;
            CurRow = CurCol = 0;
            Bg = c;
            Dirty = true;
        }

        /// <summary>QBasic's CLS. textViewOnly is CLS 2: clear just the rows
        /// inside VIEW PRINT. Graphics modes clear to attribute 0, text mode
        /// to the current background colour.</summary>
        public void ClsQB(bool textViewOnly) {
            byte c = (byte)((IsText || Mode == CustomMode) ? (Bg & ColorMask) : 0);
            int from = 0, to = Pix.Length;
            if (textViewOnly) {
                from = ViewTop * CellH * W;
                to = Math.Min(Pix.Length, (ViewBottom + 1) * CellH * W);
            }
            for (int i = from; i < to; i++) Pix[i] = c;
            CurRow = ViewTop; CurCol = 0;
            Dirty = true;
        }

        public void PSet(int x, int y, int color) {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;   // clip, never throw
            Pix[y * W + x] = (byte)(color & ColorMask);
            Dirty = true;
        }

        public int Point(int x, int y) {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H) return -1;
            return Pix[y * W + x];
        }

        public void HLine(int x0, int x1, int y, int color) {
            if ((uint)y >= (uint)H) return;
            if (x0 > x1) { int t = x0; x0 = x1; x1 = t; }
            if (x1 < 0 || x0 >= W) return;
            if (x0 < 0) x0 = 0;
            if (x1 >= W) x1 = W - 1;
            byte c = (byte)(color & ColorMask);
            int row = y * W;
            for (int x = x0; x <= x1; x++) Pix[row + x] = c;
            Dirty = true;
        }

        public void Line(int x0, int y0, int x1, int y1, int color) {
            // Bound the work for wildly off-screen coordinates.
            const int Far = 16384;
            x0 = Clamp(x0, -Far, Far); x1 = Clamp(x1, -Far, Far);
            y0 = Clamp(y0, -Far, Far); y1 = Clamp(y1, -Far, Far);
            if (y0 == y1) { HLine(x0, x1, y0, color); return; }
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true) {
                PSet(x0, y0, color);
                if (x0 == x1 && y0 == y1) break;
                int e2 = err * 2;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        public void Box(int x0, int y0, int x1, int y1, int color) {
            Line(x0, y0, x1, y0, color);
            Line(x1, y0, x1, y1, color);
            Line(x1, y1, x0, y1, color);
            Line(x0, y1, x0, y0, color);
        }

        public void BoxFill(int x0, int y0, int x1, int y1, int color) {
            if (y0 > y1) { int t = y0; y0 = y1; y1 = t; }
            if (y0 < 0) y0 = 0;
            if (y1 >= H) y1 = H - 1;
            for (int y = y0; y <= y1; y++) HLine(x0, x1, y, color);
        }

        public void Circle(int cx, int cy, int r, int color, bool filled) {
            if (r < 0) return;
            if (r == 0) { PSet(cx, cy, color); return; }
            int x = r, y = 0, err = 1 - r;
            while (x >= y) {
                if (filled) {
                    HLine(cx - x, cx + x, cy + y, color);
                    HLine(cx - x, cx + x, cy - y, color);
                    HLine(cx - y, cx + y, cy + x, color);
                    HLine(cx - y, cx + y, cy - x, color);
                } else {
                    PSet(cx + x, cy + y, color); PSet(cx - x, cy + y, color);
                    PSet(cx + x, cy - y, color); PSet(cx - x, cy - y, color);
                    PSet(cx + y, cy + x, color); PSet(cx - y, cy + x, color);
                    PSet(cx + y, cy - x, color); PSet(cx - y, cy - x, color);
                }
                y++;
                if (err < 0) err += 2 * y + 1;
                else { x--; err += 2 * (y - x) + 1; }
            }
            Dirty = true;
        }

        /// <summary>QBasic's CIRCLE in full: an ellipse or an arc of one.
        /// Angles are radians, counter-clockwise from three o'clock; a
        /// negative angle also draws the radius to that end, for pie slices.
        /// aspect is the y/x radius ratio (0 or less means the mode default).
        ///
        /// Drawn as a polyline of short Bresenham segments, so the outline is
        /// always closed and PAINT cannot leak through it.</summary>
        public void Ellipse(int cx, int cy, double r, int color,
                            bool hasStart, double start, bool hasEnd, double end, double aspect) {
            if (r < 0) return;
            if (aspect <= 0) aspect = aspect < 0 ? -aspect : CircleAspect;
            double rx = aspect <= 1 ? r : r / aspect;
            double ry = aspect <= 1 ? r * aspect : r;
            bool full = !hasStart && !hasEnd;

            if (full && Math.Abs(rx - ry) < 1e-9) {
                Circle(cx, cy, (int)Math.Round(r), color, false);
                return;
            }

            bool radStart = hasStart && start < 0, radEnd = hasEnd && end < 0;
            double a0 = hasStart ? Math.Abs(start) : 0;
            double a1 = hasEnd ? Math.Abs(end) : 2 * Math.PI;
            if (!full && a1 <= a0) a1 += 2 * Math.PI;
            double span = a1 - a0;

            int n = (int)Math.Ceiling(Math.Max(rx, ry) * span * 1.5);
            if (n < 8) n = 8;
            if (n > 4000) n = 4000;

            int px = cx + (int)Math.Round(rx * Math.Cos(a0));
            int py = cy - (int)Math.Round(ry * Math.Sin(a0));
            int sx = px, sy = py;
            PSet(px, py, color);
            for (int i = 1; i <= n; i++) {
                double a = a0 + span * i / n;
                int qx = cx + (int)Math.Round(rx * Math.Cos(a));
                int qy = cy - (int)Math.Round(ry * Math.Sin(a));
                if (qx == px && qy == py) continue;
                Line(px, py, qx, qy, color);
                px = qx; py = qy;
            }
            if (full) Line(px, py, sx, sy, color);
            if (radStart) Line(cx, cy, sx, sy, color);
            if (radEnd) Line(cx, cy, px, py, color);
            Dirty = true;
        }

        /// <summary>uBasic's PAINT: flood the region of one colour under the
        /// start point. Explicit stack, so a large fill cannot blow the C#
        /// call stack and take the editor down with it.</summary>
        public void Paint(int x, int y, int color) {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;
            byte target = Pix[y * W + x];
            byte c = (byte)(color & ColorMask);
            if (target == c) return;
            Fill(x, y, c, target, false);
        }

        /// <summary>QBasic's PAINT: fill outwards from the start point until
        /// the border colour is reached, whatever colours are in between.</summary>
        public void PaintBorder(int x, int y, int color, int border) {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;
            byte c = (byte)(color & ColorMask), b = (byte)(border & ColorMask);
            byte at = Pix[y * W + x];
            if (at == b || at == c) return;
            Fill(x, y, c, b, true);
        }

        // toBorder: fillable means "not the border and not already filled".
        // Otherwise fillable means "exactly the key colour".
        private void Fill(int x, int y, byte c, byte key, bool toBorder) {
            Stack<int> stack = new Stack<int>();
            stack.Push(y * W + x);
            while (stack.Count > 0) {
                int p = stack.Pop();
                int py = p / W;
                int px = p - py * W;
                byte v = Pix[p];
                if (toBorder ? (v == key || v == c) : (v != key)) continue;

                int row = py * W;
                int left = px;
                while (left > 0 && Fillable(Pix[row + left - 1], c, key, toBorder)) left--;
                int right = px;
                while (right < W - 1 && Fillable(Pix[row + right + 1], c, key, toBorder)) right++;

                for (int i = left; i <= right; i++) Pix[row + i] = c;
                for (int dir = -1; dir <= 1; dir += 2) {
                    int ny = py + dir;
                    if (ny < 0 || ny >= H) continue;
                    int nrow = ny * W;
                    bool run = false;
                    for (int i = left; i <= right; i++) {
                        bool f = Fillable(Pix[nrow + i], c, key, toBorder);
                        if (f && !run) stack.Push(nrow + i);
                        run = f;
                    }
                }
            }
            Dirty = true;
        }

        private static bool Fillable(byte v, byte c, byte key, bool toBorder) {
            return toBorder ? (v != key && v != c) : (v == key);
        }

        // ---- GET / PUT -------------------------------------------------------

        private int RowBytes(int w) {
            switch (ImageFormat) {
                case ImageFormat.Planar4: return 4 * ((w + 7) / 8);
                case ImageFormat.Packed2: return (w * 2 + 7) / 8;
                case ImageFormat.Packed1: return (w + 7) / 8;
                default: return w;
            }
        }

        private int WidthUnit {
            get {
                return ImageFormat == ImageFormat.Byte8 ? 8 : ImageFormat == ImageFormat.Packed2 ? 2 : 1;
            }
        }

        /// <summary>Bytes GET needs for a w x h rectangle, header included.</summary>
        public int ImageSize(int w, int h) { return 4 + h * RowBytes(w); }

        /// <summary>GET: capture a rectangle in the current mode's native
        /// image layout (a 4-byte width/height header, then packed rows).</summary>
        public byte[] GetImage(int x0, int y0, int x1, int y1) {
            if (x0 > x1) { int t = x0; x0 = x1; x1 = t; }
            if (y0 > y1) { int t = y0; y0 = y1; y1 = t; }
            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            int rb = RowBytes(w);
            byte[] d = new byte[4 + h * rb];
            int hw = w * WidthUnit;
            d[0] = (byte)hw; d[1] = (byte)(hw >> 8);
            d[2] = (byte)h;  d[3] = (byte)(h >> 8);
            int bw = (w + 7) / 8;
            for (int y = 0; y < h; y++) {
                int o = 4 + y * rb;
                for (int x = 0; x < w; x++) {
                    int c = Point(x0 + x, y0 + y);
                    if (c < 0) c = 0;
                    switch (ImageFormat) {
                        case ImageFormat.Planar4:
                            for (int p = 0; p < 4; p++)
                                if ((c & (1 << p)) != 0) d[o + p * bw + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                            break;
                        case ImageFormat.Packed2:
                            d[o + (x >> 2)] |= (byte)((c & 3) << (6 - 2 * (x & 3)));
                            break;
                        case ImageFormat.Packed1:
                            if ((c & 1) != 0) d[o + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                            break;
                        default:
                            d[o + x] = (byte)c;
                            break;
                    }
                }
            }
            return d;
        }

        /// <summary>PUT: draw image bytes produced by GetImage (or by a real
        /// QBasic). action: 0 PSET, 1 PRESET, 2 AND, 3 OR, 4 XOR. Returns
        /// false if the data is not a plausible image.</summary>
        public bool PutImage(int x0, int y0, byte[] d, int action) {
            if (d == null || d.Length < 4) return false;
            int w = (d[0] | (d[1] << 8)) / WidthUnit;
            int h = d[2] | (d[3] << 8);
            if (w <= 0 || h <= 0 || w > MaxDim || h > MaxDim) return false;
            int rb = RowBytes(w);
            if (d.Length < 4 + h * rb) return false;
            int bw = (w + 7) / 8;
            for (int y = 0; y < h; y++) {
                int yy = y0 + y;
                if ((uint)yy >= (uint)H) continue;
                int o = 4 + y * rb;
                for (int x = 0; x < w; x++) {
                    int xx = x0 + x;
                    if ((uint)xx >= (uint)W) continue;
                    int c;
                    switch (ImageFormat) {
                        case ImageFormat.Planar4:
                            c = 0;
                            for (int p = 0; p < 4; p++)
                                if ((d[o + p * bw + (x >> 3)] & (0x80 >> (x & 7))) != 0) c |= 1 << p;
                            break;
                        case ImageFormat.Packed2:
                            c = (d[o + (x >> 2)] >> (6 - 2 * (x & 3))) & 3;
                            break;
                        case ImageFormat.Packed1:
                            c = (d[o + (x >> 3)] & (0x80 >> (x & 7))) != 0 ? 1 : 0;
                            break;
                        default:
                            c = d[o + x];
                            break;
                    }
                    int at = yy * W + xx;
                    int old = Pix[at];
                    switch (action) {
                        case 1: c = ~c; break;
                        case 2: c = old & c; break;
                        case 3: c = old | c; break;
                        case 4: c = old ^ c; break;
                    }
                    Pix[at] = (byte)(c & ColorMask);
                }
            }
            Dirty = true;
            return true;
        }

        // ---- text ---------------------------------------------------------

        public void Locate(int row, int col) {
            CurRow = Clamp(row, 0, Math.Max(0, Rows - 1));
            CurCol = Clamp(col, 0, Math.Max(0, Cols - 1));
        }

        public void DrawChar(int px, int py, char ch, int fg, int bg, bool opaqueBg) {
            int code = ch;
            byte f = (byte)(fg & ColorMask), b = (byte)(bg & ColorMask);
            byte[] glyphs;
            int off, srcH;
            bool narrow = Mode == CustomMode;
            if (narrow && code >= Font6x8.First && code <= Font6x8.Last) {
                glyphs = Font6x8.Glyphs;
                off = (code - Font6x8.First) * Font6x8.CellH;
                srcH = Font6x8.CellH;
            } else {
                if (code > 255) code = '?';
                glyphs = FontCP437.Glyphs;
                off = code * FontCP437.CellH;
                srcH = FontCP437.CellH;
            }
            for (int ry = 0; ry < CellH; ry++) {
                int yy = py + ry;
                if ((uint)yy >= (uint)H) continue;
                // Tall cells (8x14, 8x16) stretch the 8-row glyph.
                byte bits = glyphs[off + (CellH == srcH ? ry : ry * srcH / CellH)];
                int row = yy * W;
                for (int rx = 0; rx < CellW; rx++) {
                    int xx = px + rx;
                    if ((uint)xx >= (uint)W) continue;
                    bool on = (bits & (0x80 >> rx)) != 0;
                    if (on) Pix[row + xx] = f;
                    else if (opaqueBg) Pix[row + xx] = b;
                }
            }
            Dirty = true;
        }

        /// <summary>Background that text is printed on. Graphics modes always
        /// use attribute 0; text mode and uBasic screens use COLOR's second
        /// argument.</summary>
        private int TextBg { get { return (IsText || Mode == CustomMode) ? Bg : 0; } }

        /// <summary>Scroll the text viewport up one line.</summary>
        public void Scroll() {
            int top = ViewTop * CellH * W;
            int end = Math.Min(Pix.Length, (ViewBottom + 1) * CellH * W);
            int shift = CellH * W;
            if (end - top > shift) Array.Copy(Pix, top + shift, Pix, top, end - top - shift);
            byte bg = (byte)(TextBg & ColorMask);
            for (int i = Math.Max(top, end - shift); i < end; i++) Pix[i] = bg;
            Dirty = true;
        }

        public void NewLine() {
            CurCol = 0;
            if (CurRow == ViewBottom) Scroll();
            else if (CurRow < Rows - 1) CurRow++;
        }

        public void Write(string s) {
            if (s == null) return;
            for (int i = 0; i < s.Length; i++) {
                char ch = s[i];
                if (ch == '\r') {
                    NewLine();
                    if (i + 1 < s.Length && s[i + 1] == '\n') i++;
                    continue;
                }
                if (ch == '\n') { NewLine(); continue; }
                if (ch == '\t') { WriteTab(); continue; }
                if (ch == '\a') continue;
                if (CurCol >= Cols) NewLine();
                DrawChar(CurCol * CellW, CurRow * CellH, ch, Fg, TextBg, true);
                CurCol++;
            }
        }

        /// <summary>PRINT's comma separator: advance to the next 14-column zone.</summary>
        public void WriteTab() {
            int zone = ((CurCol / 14) + 1) * 14;
            if (zone >= Cols) { NewLine(); return; }
            WriteSpaces(zone - CurCol);
        }

        public void WriteSpaces(int n) {
            for (int i = 0; i < n; i++) {
                if (CurCol >= Cols) NewLine();
                DrawChar(CurCol * CellW, CurRow * CellH, ' ', Fg, TextBg, true);
                CurCol++;
            }
        }

        /// <summary>PRINT TAB(n): move to 1-based column n, on the next line
        /// if the cursor is already past it.</summary>
        public void TabTo(int col1) {
            int col = Clamp(col1, 1, Cols) - 1;
            if (col < CurCol) NewLine();
            WriteSpaces(col - CurCol);
        }

        /// <summary>QBasic prints a leading space in place of the sign for
        /// non-negative numbers, and a trailing space after every number.</summary>
        public static string FormatNumber(double v) { return FormatNumber(v, false); }

        public static string FormatNumber(double v, bool dbl) {
            return (v >= 0 ? " " : "") + NumberBody(v, dbl) + " ";
        }

        /// <summary>Digits of a number the way QBasic writes them: 7
        /// significant digits for SINGLE, 16 for DOUBLE, no leading zero
        /// before the decimal point.</summary>
        public static string NumberBody(double v, bool dbl) {
            if (double.IsNaN(v)) return "NaN";
            if (double.IsInfinity(v)) return v > 0 ? "Inf" : "-Inf";
            if (!dbl) v = (float)v;
            string body;
            if (v == Math.Floor(v) && Math.Abs(v) < (dbl ? 1e15 : 1e7))
                body = ((long)v).ToString(CultureInfo.InvariantCulture);
            else {
                body = v.ToString(dbl ? "G15" : "G7", CultureInfo.InvariantCulture);
                if (body.StartsWith("0.")) body = body.Substring(1);
                else if (body.StartsWith("-0.")) body = "-" + body.Substring(2);
            }
            return body;
        }

        public static string FormatInt(int v) {
            return (v >= 0 ? " " : "") + v.ToString(CultureInfo.InvariantCulture) + " ";
        }

        /// <summary>Copy the indexed buffer out as RGBA32, bottom-up so it maps
        /// straight onto a Unity texture without a flip.</summary>
        public void BlitRgba32(byte[] dst, bool flipY) {
            int n = W * H;
            if (dst == null || dst.Length < n * 4) throw new ArgumentException("dst too small");
            for (int y = 0; y < H; y++) {
                int srcRow = y * W;
                int dstRow = (flipY ? (H - 1 - y) : y) * W;
                for (int x = 0; x < W; x++) {
                    int pi = Pix[srcRow + x] * 3;
                    int d = (dstRow + x) * 4;
                    dst[d + 0] = Palette[pi + 0];
                    dst[d + 1] = Palette[pi + 1];
                    dst[d + 2] = Palette[pi + 2];
                    dst[d + 3] = 255;
                }
            }
        }
    }
}
