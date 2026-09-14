using System;
using System.IO;
using System.Collections.Generic;
using System.IO.Compression;
using UBasic;

/// <summary>Headless driver used to develop and test uBasic without Unity.
/// Runs the program frame by frame exactly as UBasicRunner does, then writes
/// the framebuffer to a PNG.</summary>
public static class TestHost {

    public static int Main(string[] argv) {
        if (argv.Length >= 1 && argv[0] == "--selftest") return SelfTest();
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

        AudioRecorder audio = new AudioRecorder(44100);

        int frames = 0;
        while (frames < maxFrames) {
            float now = frames / 60f;
            vm.Time = now;

            // Stand in for the Unity audio adapter: take whatever the program
            // queued, lay it down on a virtual timeline, and report busy while
            // that timeline is ahead of the frame clock. This exercises the
            // real blocking path rather than bypassing it.
            audio.Pump(vm.Sound, now);
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
        // The program can END while MB background music is still queued. Unity
        // keeps pumping the adapter from Update, so do the same here or the
        // tail of the music is silently cut off.
        for (int extra = 0; extra < 3600 && audio.Pending > 0; extra++)
            audio.Pump(vm.Sound, (frames + extra) / 60f);

        Console.WriteLine("ran " + frames + " frame(s), live strings: " + vm.Strings.Count);
        WritePng(outPng, scr);
        Console.WriteLine("wrote " + outPng);
        if (audio.HasAudio) {
            string wav = Path.ChangeExtension(outPng, ".wav");
            audio.WriteWav(wav);
            Console.WriteLine("wrote " + wav + " (" + audio.BusyUntil.ToString("0.00") + "s, " +
                audio.NoteCount + " notes)");
        }
        return 0;
    }

    /// <summary>Stands in for the Unity audio adapter. Drives the *real*
    /// SoundScheduler against a virtual clock and renders whatever it schedules
    /// as 16-bit PCM, so note timing, the blocking path and the MML parser are
    /// all exercised for real in CI.</summary>
    private class AudioRecorder {
        private readonly int _rate;
        private readonly SoundScheduler _sched = new SoundScheduler(4);
        private readonly List<ScheduledNote> _starts = new List<ScheduledNote>();
        private float[] _buf = new float[0];
        public int NoteCount;
        public double BusyUntil { get { return _sched.BusyUntil; } }
        public bool HasAudio { get { return NoteCount > 0; } }
        public int Pending { get { return _sched.PendingCount; } }

        public AudioRecorder(int rate) { _rate = rate; }

        public void Pump(SoundDevice dev, double now) {
            _sched.Update(dev, now, _starts);
            for (int i = 0; i < _starts.Count; i++) { Render(_starts[i]); NoteCount++; }
        }

        private void Render(ScheduledNote sn) {
            float[] cycle = SoundDevice.BuildLoopCycle(sn.Freq, _rate, 0.28f, 4096);
            int start = (int)(sn.Start * _rate);
            int count = (int)((sn.End - sn.Start) * _rate);
            int need = start + count + 1;
            if (need > _buf.Length) {
                int n = Math.Max(_rate, _buf.Length);
                while (n < need) n *= 2;
                Array.Resize(ref _buf, n);
            }
            for (int i = 0; i < count; i++) _buf[start + i] += cycle[i % cycle.Length];
        }

        public void WriteWav(string path) {
            int n = (int)(BusyUntil * _rate);
            if (n > _buf.Length) n = _buf.Length;
            using (FileStream fs = File.Create(path))
            using (BinaryWriter w = new BinaryWriter(fs)) {
                int dataBytes = n * 2;
                w.Write(new char[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new char[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(_rate); w.Write(_rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new char[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                for (int i = 0; i < n; i++) {
                    float v = _buf[i];
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    w.Write((short)(v * 32767f));
                }
            }
        }
    }

    /// <summary>Scheduler regression tests. The idle case is the one that
    /// shipped broken: an idle scheduler reporting busy blocks the VM forever
    /// on the first SOUND.</summary>
    private static int SelfTest() {
        int fail = 0;
        List<ScheduledNote> starts = new List<ScheduledNote>();

        SoundDevice dev = new SoundDevice();
        SoundScheduler sch = new SoundScheduler(4);

        // 1. idle must never report busy, over many frames
        for (int f = 0; f < 200; f++) {
            sch.Update(dev, f / 60.0, starts);
            if (dev.HostBusy) { Console.Error.WriteLine("FAIL: idle scheduler reported busy at frame " + f); fail++; break; }
        }
        Console.WriteLine(fail == 0 ? "ok   idle scheduler stays not-busy" : "FAIL idle");

        // 2. a note makes it busy, and it clears afterwards
        dev.Sound(440f, 18.2f);                       // one second
        double t = 200 / 60.0;
        sch.Update(dev, t, starts);
        bool busyNow = dev.HostBusy;
        bool cleared = false;
        for (int f = 201; f < 200 + 300; f++) {
            sch.Update(dev, f / 60.0, starts);
            if (!dev.HostBusy) { cleared = true; t = f / 60.0; break; }
        }
        if (!busyNow) { Console.WriteLine("FAIL a queued note did not set busy"); fail++; }
        else if (!cleared) { Console.WriteLine("FAIL busy never cleared"); fail++; }
        else Console.WriteLine("ok   busy set, then cleared after ~" + (t - 200 / 60.0).ToString("0.00") + "s");

        // 3. an inaudible delay tone still blocks for its full duration
        SoundDevice d2 = new SoundDevice();
        SoundScheduler s2 = new SoundScheduler(4);
        d2.Sound(32000f, 18.2f);
        s2.Update(d2, 0, starts);
        if (starts.Count != 0) { Console.WriteLine("FAIL inaudible tone allocated a voice"); fail++; }
        double until = 0;
        for (int f = 1; f < 300; f++) {
            s2.Update(d2, f / 60.0, starts);
            if (!d2.HostBusy) { until = f / 60.0; break; }
        }
        if (until < 0.95 || until > 1.2) { Console.WriteLine("FAIL delay blocked for " + until.ToString("0.00") + "s, expected ~1.0"); fail++; }
        else Console.WriteLine("ok   SOUND 32000 blocks " + until.ToString("0.00") + "s silently");

        // 4. background (MB) music must not block
        SoundDevice d3 = new SoundDevice();
        d3.Play("MB T120 O4 L4 CDEFG");
        if (d3.IsBusy) { Console.WriteLine("FAIL MB music blocked the program"); fail++; }
        else Console.WriteLine("ok   MB music does not block");

        // 5. foreground (MF) music must block
        SoundDevice d4 = new SoundDevice();
        d4.Play("MF T120 O4 L4 C");
        if (!d4.IsBusy) { Console.WriteLine("FAIL MF music did not block"); fail++; }
        else Console.WriteLine("ok   MF music blocks");

        Console.WriteLine(fail == 0 ? "\nall scheduler tests passed" : "\n" + fail + " FAILURE(S)");
        return fail == 0 ? 0 : 1;
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
