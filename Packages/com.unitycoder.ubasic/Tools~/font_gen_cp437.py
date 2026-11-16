#!/usr/bin/env python3
"""Builds FontCP437.cs: an 8x8 font covering all 256 IBM code page 437 glyphs.

Glyph bitmaps come from Daniel Hepper's public-domain font8x8 headers
(https://github.com/dhepper/font8x8), re-indexed from Unicode to CP437.
The handful of CP437 symbols font8x8 lacks are drawn by hand below.

usage: font_gen_cp437.py <dir with font8x8_*.h> <out.cs>
"""
import re, sys, os

src, out = sys.argv[1], sys.argv[2]
glyphs = {}

def load(name, base):
    rows = re.findall(r'\{\s*((?:0x[0-9A-Fa-f]{2}\s*,\s*){7}0x[0-9A-Fa-f]{2})\s*\}\s*,?\s*//\s*(.*)',
                      open(os.path.join(src, name)).read())
    for i, (data, comment) in enumerate(rows):
        b = [int(x, 16) for x in re.findall(r'0x[0-9A-Fa-f]{2}', data)]
        # font8x8 stores bit 0 as the leftmost pixel; we want bit 7 leftmost.
        b = [int('{:08b}'.format(v)[::-1], 2) for v in b]
        if base is None:
            m = re.match(r'U\+([0-9A-Fa-f]{4})', comment)
            if m: glyphs.setdefault(int(m.group(1), 16), b)
            elif 'less than or equal' in comment: glyphs.setdefault(0x2264, b)
            elif 'greater than or equal' in comment: glyphs.setdefault(0x2265, b)
            elif 'superscript a' in comment: glyphs.setdefault(0xAA, b)
            elif 'superscript 0' in comment: glyphs.setdefault(0xBA, b)
        else:
            glyphs.setdefault(base + i, b)

load('font8x8_basic.h', 0x0000)
load('font8x8_ext_latin.h', 0x00A0)
load('font8x8_greek.h', 0x0390)
load('font8x8_box.h', 0x2500)
load('font8x8_block.h', 0x2580)
load('font8x8_misc.h', None)

def art(cp, rows):
    assert len(rows) == 8, hex(cp)
    glyphs[cp] = [int(r.replace('.', '0').replace('#', '1'), 2) for r in rows]

art(0x263A, ['.######.','#......#','#.#..#.#','#......#','#.####.#','#..##..#','#......#','.######.'])
art(0x263B, ['.######.','########','##.##.##','########','##....##','###..###','########','.######.'])
art(0x2665, ['.##.##..','#######.','#######.','#######.','.#####..','..###...','...#....','........'])
art(0x2666, ['...#....','..###...','.#####..','#######.','.#####..','..###...','...#....','........'])
art(0x2663, ['..###...','.#####..','..###...','#######.','#######.','.#.#.#..','...#....','..###...'])
art(0x2660, ['...#....','..###...','.#####..','#######.','#######.','.#####..','...#....','..###...'])
art(0x2022, ['........','........','...##...','..####..','..####..','...##...','........','........'])
art(0x25D8, ['########','########','###..###','##....##','##....##','###..###','########','########'])
art(0x25CB, ['........','..####..','.##..##.','.#....#.','.#....#.','.##..##.','..####..','........'])
art(0x25D9, ['########','##....##','#..##..#','#.####.#','#.####.#','#..##..#','##....##','########'])
art(0x2642, ['....####','.....###','....####','.#####.#','##..##..','##..##..','##..##..','.####...'])
art(0x2640, ['..####..','.##..##.','.##..##.','.##..##.','..####..','...##...','.######.','...##...'])
art(0x266A, ['..######','..##..##','..######','..##....','..##....','.###....','####....','###.....'])
art(0x266B, ['.#######','.##...##','.#######','.##...##','.##...##','.##..###','###..##.','##......'])
art(0x263C, ['...##...','##.##.##','..####..','###..###','###..###','..####..','##.##.##','...##...'])
art(0x25BA, ['#.......','###.....','#####...','#######.','#####...','###.....','#.......','........'])
art(0x25C4, ['......#.','....###.','..#####.','#######.','..#####.','....###.','......#.','........'])
art(0x2195, ['...##...','..####..','.######.','...##...','...##...','.######.','..####..','...##...'])
art(0x203C, ['.##..##.','.##..##.','.##..##.','.##..##.','.##..##.','........','.##..##.','........'])
art(0x00B6, ['.#######','##.##.##','##.##.##','.####.##','...##.##','...##.##','...##.##','........'])
art(0x00A7, ['..#####.','.##...##','..###...','.##.##..','.##.##..','..###...','##..##..','.####...'])
art(0x25AC, ['........','........','........','........','.######.','.######.','.######.','........'])
art(0x21A8, ['...##...','..####..','.######.','...##...','.######.','..####..','...##...','########'])
art(0x2191, ['...##...','..####..','.######.','...##...','...##...','...##...','...##...','........'])
art(0x2193, ['...##...','...##...','...##...','...##...','.######.','..####..','...##...','........'])
art(0x2192, ['........','...##...','....##..','#######.','....##..','...##...','........','........'])
art(0x2190, ['........','..##....','.##.....','#######.','.##.....','..##....','........','........'])
art(0x221F, ['........','........','##......','##......','##......','#######.','........','........'])
art(0x2194, ['........','..#..#..','.##..##.','########','.##..##.','..#..#..','........','........'])
art(0x25B2, ['........','...##...','..####..','.######.','########','########','........','........'])
art(0x25BC, ['........','########','########','.######.','..####..','...##...','........','........'])
art(0x2302, ['........','...#....','..###...','.##.##..','##...##.','##...##.','#######.','........'])
art(0x2261, ['........','.######.','........','.######.','........','.######.','........','........'])
art(0x2320, ['....###.','...##.##','...##.##','...##...','...##...','...##...','...##...','...##...'])
art(0x2321, ['...##...','...##...','...##...','...##...','...##...','##.##...','##.##...','.###....'])
art(0x2248, ['........','.###.##.','##.###..','........','.###.##.','##.###..','........','........'])
art(0x2219, ['........','........','........','...##...','...##...','........','........','........'])
art(0x221A, ['....####','....##..','....##..','....##..','###.##..','.##.##..','..####..','...###..'])
art(0x207F, ['.####...','.##.##..','.##.##..','.##.##..','.##.##..','........','........','........'])
art(0x25A0, ['........','........','..####..','..####..','..####..','..####..','........','........'])
art(0x221E, ['........','........','.###.##.','##.##.##','##.##.##','.##.###.','........','........'])
art(0x2229, ['.####...','##..##..','##..##..','##..##..','##..##..','##..##..','##..##..','........'])
# Shade characters: font8x8's are fine but make sure the three densities exist.
art(0x2591, ['..#...#.','#...#...','..#...#.','#...#...','..#...#.','#...#...','..#...#.','#...#...'])
art(0x2592, ['.#.#.#.#','#.#.#.#.','.#.#.#.#','#.#.#.#.','.#.#.#.#','#.#.#.#.','.#.#.#.#','#.#.#.#.'])
art(0x2593, ['##.###.#','.###.###','##.###.#','.###.###','##.###.#','.###.###','##.###.#','.###.###'])

low = "\u0000☺☻♥♦♣♠•◘○◙♂♀♪♫☼" \
      "►◄↕‼¶§▬↨↑↓→←∟↔▲▼"
table = [ord(c) for c in low] + list(range(32, 127)) + [0x2302] + \
        [ord(bytes([i]).decode('cp437')) for i in range(128, 256)]

missing = []
lines = []
for code, cp in enumerate(table):
    g = glyphs.get(cp)
    if code in (0, 32, 255): g = [0] * 8
    if g is None:
        missing.append('%d U+%04X' % (code, cp)); g = [0] * 8
    lines.append('            ' + ', '.join('0x%02X' % v for v in g) + ', // %d' % code)

uni = ', '.join('0x%04X' % cp for cp in table)
uni_lines = []
for i in range(0, 256, 8):
    uni_lines.append('            ' + ', '.join('(char)0x%04X' % cp for cp in table[i:i+8]) + ',')

with open(out, 'w') as f:
    f.write('''// AUTO-GENERATED by Tools~/font_gen_cp437.py -- do not edit by hand.
// Glyph bitmaps: font8x8 by Daniel Hepper (public domain), re-indexed to CP437.
using System.Collections.Generic;

namespace Unitycoder.UBasic {
    /// <summary>8x8 bitmap font covering all 256 characters of IBM code page
    /// 437, the character set of QBasic's text and graphics screens.
    /// One byte per row, bit 7 = leftmost pixel. 8 bytes per glyph. Taller
    /// text cells (8x14, 8x16) stretch these rows vertically.</summary>
    public static class FontCP437 {
        public const int CellW = 8;
        public const int CellH = 8;

        public static readonly byte[] Glyphs = {
''' + '\n'.join(lines) + '''
        };
    }

    /// <summary>Unicode to CP437 and back. Inside the VM a string character is
    /// a CP437 code (0..255), exactly as in QBasic, so CHR$(219) is the solid
    /// block. Source text is Unicode and is converted when it is lexed.</summary>
    public static class Cp437 {
        public static readonly char[] ToUnicode = {
''' + '\n'.join(uni_lines) + '''
        };

        private static Dictionary<char, char> _from;

        /// <summary>Map one Unicode character to its CP437 code. Characters
        /// with no CP437 equivalent become '?'.</summary>
        public static char FromUnicode(char c) {
            if (c < 127) return c;
            if (_from == null) {
                Dictionary<char, char> d = new Dictionary<char, char>();
                for (int i = 255; i >= 1; i--) {
                    if (i >= 32 && i < 127) continue;
                    d[ToUnicode[i]] = (char)i;
                }
                _from = d;
            }
            char r;
            return _from.TryGetValue(c, out r) ? r : '?';
        }

        /// <summary>Decode raw CP437 bytes (a .BAS file saved by DOS QBasic)
        /// into Unicode text. Control characters below 32 are kept as they
        /// are so line endings survive; the DOS end-of-file mark is dropped.</summary>
        public static string Decode(byte[] bytes) {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(bytes.Length);
            for (int i = 0; i < bytes.Length; i++) {
                byte b = bytes[i];
                if (b == 0x1A) continue;
                sb.Append(b < 127 ? (char)b : ToUnicode[b]);
            }
            return sb.ToString();
        }
    }
}
''')
print('missing:', missing)
