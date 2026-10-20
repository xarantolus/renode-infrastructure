//
// Copyright (c) 2026 Philipp Erhardt <philipp.erhardt@tum.de>
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Peripherals.CAN;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    // bxCAN mode handling: RM0432 Rev 9 section 55.4; the sequence is ST's HAL_CAN_Init
    // (stm32l4xx_hal_can.c), which Zephyr's can_stm32_bxcan follows
    [TestFixture]
    public class STMCAN_InitModeTest
    {
        [SetUp]
        public void SetUp()
        {
            can = new STMCAN();
            can.Reset();
        }

        [Test]
        public void ShouldStartInSleepMode()
        {
            Assert.AreEqual(SLAK, can.ReadDoubleWord(MSR) & (SLAK | INAK));
        }

        [Test]
        public void ShouldEnterInitializationBeforeLeavingSleep()
        {
            can.WriteDoubleWord(MCR, can.ReadDoubleWord(MCR) | INRQ);
            Assert.AreEqual(INAK, can.ReadDoubleWord(MSR) & INAK);

            can.WriteDoubleWord(MCR, can.ReadDoubleWord(MCR) & ~SLEEP);
            Assert.AreEqual(INAK, can.ReadDoubleWord(MSR) & (SLAK | INAK));

            can.WriteDoubleWord(MCR, can.ReadDoubleWord(MCR) & ~INRQ);
            Assert.AreEqual(0u, can.ReadDoubleWord(MSR) & (SLAK | INAK));
        }

        private STMCAN can;

        private const long MCR = 0x00;
        private const long MSR = 0x04;

        private const uint INRQ = 1u << 0;
        private const uint SLEEP = 1u << 1;
        private const uint INAK = 1u << 0;
        private const uint SLAK = 1u << 1;
    }
}
