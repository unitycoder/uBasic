using System;
using System.IO;
using System.IO.Compression;
using UBasic;

/// <summary>Headless driver used to develop and test uBasic without Unity.
/// Runs the program frame by frame exactly as UBasicRunner does, then writes
/// the framebuffer to a PNG.</summary>
public static class TestHost {

    public static int Main(string[] argv) {
        if (argv.Length < 1) {
            Console.Error.WriteLine("usage: TestHost <file.ubas> [outfile.png] [frames]");
            return 2;
        }
        string path = argv[0];
        string outPng = argv.Length > 1 ? argv[1] : "out.png";
        int maxFrames = argv.Length > 2 ? int.Parse(argv[2]) : 240;

        string src = File.ReadAllText(path);
        Chunk chunk;
        try {
            chunk = Compiler.Compile(src);
        } catch (UBasicError e) {
            Console.Error.WriteLine("COMPILE ERROR: " + e.Message);
            return 1;
        }
        Console.WriteLine("compiled: " + chunk.Code.Count + " instrs, " +
            chunk.GlobalTypes.Count + " globals, " + chunk.Funcs.Count + " procs, " +
            chunk.Arrays.Count + " arrays");

        Screen scr = new Screen(320, 200);
        VM vm = new VM(chunk, scr);
        vm.Rng = new Random(1);

        int frames = 0;
        while (frames < maxFrames) {
            vm.Time = frames / 60f;
            RunState st = vm.Run(200000);
            if (st == RunState.Halted) break;
            if (st == RunState.Faulted) {
                Console.Error.WriteLine("RUNTIME ERROR: " + vm.FaultMessage);
                WritePng(outPng, scr);
                return 1;
            }
            if (st == RunState.Yielded) {
                Console.Error.WriteLine("frame budget exhausted at frame " + frames +
                    " (no WAIT in a long loop?)");
                break;
            }
            frames++;
        }
        Console.WriteLine("ran " + frames + " frame(s), live strings: " + vm.Strings.Count);
        WritePng(outPng, scr);
        Console.WriteLine("wrote " + outPng);
        return 0;
    }

    // ---- minimal PNG writer (RGB8, no filtering) ----

    private static void WritePng(string path, Screen s) {
        int w = s.W, h = s.H;
        byte[] raw = new byte[h * (1 + w * 3)];
        int o = 0;
        for (int y = 0; y < h; y++) {
            raw[o++] = 0;
            for (int x = 0; x < w; x++) {
                int pi = s.Pix[y * w + x] * 3;
                raw[o++] = s.Palette[pi + 0];
                raw[o++] = s.Palette[pi + 1];
                raw[o++] = s.Palette[pi + 2];
            }
        }
        using (FileStream fs = File.Create(path)) {
            fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            MemoryStream ihdr = new MemoryStream();
            WriteBE(ihdr, w); WriteBE(ihdr, h);
            ihdr.WriteByte(8); ihdr.WriteByte(2); ihdr.WriteByte(0); ihdr.WriteByte(0); ihdr.WriteByte(0);
            Chunk_(fs, "IHDR", ihdr.ToArray());
            Chunk_(fs, "IDAT", ZlibCompress(raw));
            Chunk_(fs, "IEND", new byte[0]);
        }
    }

    private static void WriteBE(Stream s, int v) {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    private static void Chunk_(Stream fs, string type, byte[] data) {
        WriteBE(fs, data.Length);
        byte[] td = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, td, 4, data.Length);
        fs.Write(td, 0, td.Length);
        WriteBE(fs, unchecked((int)Crc32(td)));
    }

    private static byte[] ZlibCompress(byte[] data) {
        MemoryStream ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x01);
        using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Compress, true))
            ds.Write(data, 0, data.Length);
        uint a = 1, b = 0;
        for (int i = 0; i < data.Length; i++) { a = (a + data[i]) % 65521; b = (b + a) % 65521; }
        uint adler = (b << 16) | a;
        ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    private static uint[] _crcTable;
    private static uint Crc32(byte[] d) {
        if (_crcTable == null) {
            _crcTable = new uint[256];
            for (uint n = 0; n < 256; n++) {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                _crcTable[n] = c;
            }
        }
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < d.Length; i++) crc = _crcTable[(crc ^ d[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
