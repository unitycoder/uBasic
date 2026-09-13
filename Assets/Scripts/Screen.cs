using System;
using System.Collections.Generic;
using System.Text;

namespace UBasic {

    /// <summary>The uBasic "screen": a palette-indexed framebuffer plus a text
    /// cursor. Deliberately engine-free -- Unity only ever reads Pix and Palette
    /// to fill a Texture2D, so the whole language can be tested headlessly.</summary>
    public class Screen {
        public const int CharW = Font6x8.CellW;
        public const int CharH = Font6x8.CellH;
        public const int MaxDim = 2048;

        public int W { get; private set; }
        public int H { get; private set; }
        public byte[] Pix { get; private set; }     // one palette index per pixel
        public readonly byte[] Palette = new byte[16 * 3];
        public bool Dirty = true;

        public int Fg = 15, Bg = 0;
        public int CurRow, CurCol;

        public int Cols { get { return W / CharW; } }
        public int Rows { get { return H / CharH; } }

        private static readonly int[] DefaultPalette = {
            0x000000, 0x0000AA, 0x00AA00, 0x00AAAA, 0xAA0000, 0xAA00AA, 0xAA5500, 0xAAAAAA,
            0x555555, 0x5555FF, 0x55FF55, 0x55FFFF, 0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF
        };

        public Screen(int w, int h) {
            ResetPalette();
            Resize(w, h);
        }

        public void ResetPalette() {
            for (int i = 0; i < 16; i++) {
                int c = DefaultPalette[i];
                Palette[i * 3 + 0] = (byte)((c >> 16) & 0xFF);
                Palette[i * 3 + 1] = (byte)((c >> 8) & 0xFF);
                Palette[i * 3 + 2] = (byte)(c & 0xFF);
            }
            Dirty = true;
        }

        public void SetPalette(int i, int r, int g, int b) {
            if (i < 0 || i > 15) return;
            Palette[i * 3 + 0] = (byte)Clamp(r, 0, 255);
            Palette[i * 3 + 1] = (byte)Clamp(g, 0, 255);
            Palette[i * 3 + 2] = (byte)Clamp(b, 0, 255);
            Dirty = true;
        }

        public void Resize(int w, int h) {
            w = Clamp(w, CharW, MaxDim);
            h = Clamp(h, CharH, MaxDim);
            if (w == W && h == H && Pix != null) return;
            W = w; H = h;
            Pix = new byte[W * H];
            CurRow = CurCol = 0;
            Fg = 15; Bg = 0;
            Dirty = true;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        // ---- pixels -------------------------------------------------------

        public void Cls(int color) {
            byte c = (byte)(color & 15);
            // Array.Fill is not available on older targets Unity may compile against.
            for (int i = 0; i < Pix.Length; i++) Pix[i] = c;
            CurRow = CurCol = 0;
            Bg = c;
            Dirty = true;
        }

        public void PSet(int x, int y, int color) {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;   // clip, never throw
            Pix[y * W + x] = (byte)(color & 15);
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
            byte c = (byte)(color & 15);
            int row = y * W;
            for (int x = x0; x <= x1; x++) Pix[row + x] = c;
            Dirty = true;
        }

        public void Line(int x0, int y0, int x1, int y1, int color) {
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

        /// <summary>Scanline flood fill. Explicit stack, so a large fill cannot
        /// blow the C# call stack and take the editor down with it.</summary>
        public void Paint(int x, int y, int color) {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;
            byte target = Pix[y * W + x];
            byte c = (byte)(color & 15);
            if (target == c) return;

            Stack<int> stack = new Stack<int>();
            stack.Push(y * W + x);
            while (stack.Count > 0) {
                int p = stack.Pop();
                int py = p / W;
                int px = p - py * W;

                int left = px;
                while (left > 0 && Pix[py * W + left - 1] == target) left--;
                int right = px;
                while (right < W - 1 && Pix[py * W + right + 1] == target) right++;

                for (int i = left; i <= right; i++) {
                    Pix[py * W + i] = c;
                    if (py > 0 && Pix[(py - 1) * W + i] == target) stack.Push((py - 1) * W + i);
                    if (py < H - 1 && Pix[(py + 1) * W + i] == target) stack.Push((py + 1) * W + i);
                }
            }
            Dirty = true;
        }

        // ---- text ---------------------------------------------------------

        public void Locate(int row, int col) {
            CurRow = Clamp(row, 0, Math.Max(0, Rows - 1));
            CurCol = Clamp(col, 0, Math.Max(0, Cols - 1));
        }

        public void DrawChar(int px, int py, char ch, int fg, int bg, bool opaqueBg) {
            int code = ch;
            if (code < Font6x8.First || code > Font6x8.Last) code = '?';
            int off = (code - Font6x8.First) * Font6x8.CellH;
            for (int ry = 0; ry < Font6x8.CellH; ry++) {
                byte bits = Font6x8.Glyphs[off + ry];
                int yy = py + ry;
                if ((uint)yy >= (uint)H) continue;
                for (int rx = 0; rx < Font6x8.CellW; rx++) {
                    int xx = px + rx;
                    if ((uint)xx >= (uint)W) continue;
                    bool on = (bits & (0x80 >> rx)) != 0;
                    if (on) Pix[yy * W + xx] = (byte)(fg & 15);
                    else if (opaqueBg) Pix[yy * W + xx] = (byte)(bg & 15);
                }
            }
            Dirty = true;
        }

        public void Scroll() {
            int shift = CharH * W;
            Array.Copy(Pix, shift, Pix, 0, Pix.Length - shift);
            for (int i = Pix.Length - shift; i < Pix.Length; i++) Pix[i] = (byte)Bg;
            Dirty = true;
        }

        public void NewLine() {
            CurCol = 0;
            CurRow++;
            if (CurRow >= Rows) { CurRow = Rows - 1; Scroll(); }
        }

        public void Write(string s) {
            if (s == null) return;
            for (int i = 0; i < s.Length; i++) {
                char ch = s[i];
                if (ch == '\n') { NewLine(); continue; }
                if (ch == '\t') { WriteTab(); continue; }
                if (CurCol >= Cols) NewLine();
                DrawChar(CurCol * CharW, CurRow * CharH, ch, Fg, Bg, true);
                CurCol++;
            }
        }

        /// <summary>PRINT's comma separator: advance to the next 14-column zone.</summary>
        public void WriteTab() {
            int zone = ((CurCol / 14) + 1) * 14;
            if (zone >= Cols) { NewLine(); return; }
            while (CurCol < zone) {
                DrawChar(CurCol * CharW, CurRow * CharH, ' ', Fg, Bg, true);
                CurCol++;
            }
        }

        /// <summary>QBasic prints a leading space in place of the sign for
        /// non-negative numbers, and a trailing space after every number.</summary>
        public static string FormatNumber(float v) {
            string body;
            if (v == (int)v && Math.Abs(v) < 1e9f) body = ((int)v).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            else body = v.ToString("G7", System.Globalization.CultureInfo.InvariantCulture);
            return (v >= 0 ? " " : "") + body + " ";
        }

        public static string FormatInt(int v) {
            return (v >= 0 ? " " : "") + v.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + " ";
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
