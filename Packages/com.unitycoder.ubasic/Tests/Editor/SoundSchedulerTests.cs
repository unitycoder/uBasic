using System.Collections.Generic;
using NUnit.Framework;

namespace Unitycoder.UBasic.Tests {

    /// <summary>Regression tests for audio timing.
    ///
    /// These exist because this logic once lived inside the Unity audio
    /// adapter, where it could not be run headlessly. A bug that reported
    /// "busy" forever while idle therefore survived a full test pass and
    /// blocked every program permanently on its first SOUND. The timeline now
    /// lives in SoundScheduler, which takes its clock as a parameter, so it is
    /// testable both here and from the headless harness in Tools~.</summary>
    public class SoundSchedulerTests {

        private readonly List<ScheduledNote> _starts = new List<ScheduledNote>();

        [Test]
        public void IdleSchedulerNeverReportsBusy() {
            SoundDevice dev = new SoundDevice();
            SoundScheduler sch = new SoundScheduler(4);

            for (int f = 0; f < 200; f++) {
                sch.Update(dev, f / 60.0, _starts);
                Assert.IsFalse(dev.HostBusy,
                    "idle scheduler reported busy at frame " + f +
                    " -- this is what stalls the VM on its first SOUND");
            }
        }

        [Test]
        public void QueuedNoteSetsBusyThenClears() {
            SoundDevice dev = new SoundDevice();
            SoundScheduler sch = new SoundScheduler(4);

            dev.Sound(440f, SoundDevice.TicksPerSecond);     // one second
            sch.Update(dev, 0, _starts);
            Assert.IsTrue(dev.HostBusy, "a queued note should mark the device busy");
            Assert.AreEqual(1, _starts.Count, "an audible note should get a voice");

            double clearedAt = -1;
            for (int f = 1; f < 300; f++) {
                sch.Update(dev, f / 60.0, _starts);
                if (!dev.HostBusy) { clearedAt = f / 60.0; break; }
            }
            Assert.Greater(clearedAt, 0, "busy never cleared");
            Assert.That(clearedAt, Is.EqualTo(1.05).Within(0.15),
                "a one-second note should free the device after about a second");
        }

        [Test]
        public void InaudibleDelayToneBlocksButUsesNoVoice() {
            // The classic QBasic "SOUND 32000, 18.2" one-second pause.
            SoundDevice dev = new SoundDevice();
            SoundScheduler sch = new SoundScheduler(4);

            dev.Sound(32000f, SoundDevice.TicksPerSecond);
            sch.Update(dev, 0, _starts);
            Assert.AreEqual(0, _starts.Count, "an inaudible tone should not take a voice");

            double until = -1;
            for (int f = 1; f < 300; f++) {
                sch.Update(dev, f / 60.0, _starts);
                if (!dev.HostBusy) { until = f / 60.0; break; }
            }
            Assert.That(until, Is.EqualTo(1.05).Within(0.15),
                "the delay idiom should still block for its full duration");
        }

        [Test]
        public void AboveHearingProducesSilenceNotAliasing() {
            // Sampled at 44.1 kHz a 32 kHz square wave would fold down to an
            // audible screech, so it must be generated as silence instead.
            float[] data = SoundDevice.BuildLoopCycle(32000f, 44100, 0.28f, 4096);
            foreach (float v in data)
                Assert.AreEqual(0f, v, "above 20 kHz must render as silence");
        }

        [Test]
        public void BackgroundMusicDoesNotBlock() {
            SoundDevice dev = new SoundDevice();
            dev.Play("MB T120 O4 L4 CDEFG");
            Assert.IsFalse(dev.IsBusy, "MB music should let the program carry on");
        }

        [Test]
        public void ForegroundMusicBlocks() {
            SoundDevice dev = new SoundDevice();
            dev.Play("MF T120 O4 L4 C");
            Assert.IsTrue(dev.IsBusy, "MF music should block the program");
        }
    }
}
