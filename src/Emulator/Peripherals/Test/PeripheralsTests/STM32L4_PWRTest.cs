//
// Copyright (c) 2026 Philipp Erhardt <philipp.erhardt@tum.de>
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Miscellaneous;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    // Offsets, bits and reset values: RM0432 Rev 9 section 5.4 and stm32l4r5xx.h
    [TestFixture]
    public class STM32L4_PWRTest
    {
        [SetUp]
        public void SetUp()
        {
            pwr = new STM32L4_PWR(new Machine());
        }

        [Test]
        public void ShouldResetToRange1WithoutBoost()
        {
            Assert.AreEqual(0x00000200, pwr.ReadDoubleWord(CR1));
            Assert.AreEqual(0x00008000, pwr.ReadDoubleWord(CR3));
            Assert.AreEqual(0x00000100, pwr.ReadDoubleWord(CR5));
        }

        [Test]
        public void ShouldNeverReportVoltageScalingInProgress()
        {
            pwr.WriteDoubleWord(CR1, VOS_RANGE2);
            Assert.AreEqual(0u, pwr.ReadDoubleWord(SR2) & VOSF);

            // Boost mode: R1MODE cleared while in Range 1
            pwr.WriteDoubleWord(CR1, VOS_RANGE1);
            pwr.WriteDoubleWord(CR5, 0);
            Assert.AreEqual(0u, pwr.ReadDoubleWord(SR2) & VOSF);
            Assert.AreEqual(0u, pwr.ReadDoubleWord(CR5));
        }

        [Test]
        public void ShouldReportRegulatorModeFromLpr()
        {
            Assert.AreEqual(0u, pwr.ReadDoubleWord(SR2) & REGLPF);

            pwr.WriteDoubleWord(CR1, pwr.ReadDoubleWord(CR1) | LPR);
            Assert.AreEqual(REGLPF, pwr.ReadDoubleWord(SR2) & REGLPF);
        }

        [Test]
        public void ShouldKeepBackupDomainWriteProtectionBit()
        {
            pwr.WriteDoubleWord(CR1, pwr.ReadDoubleWord(CR1) | DBP);

            Assert.AreEqual(DBP, pwr.ReadDoubleWord(CR1) & DBP);
        }

        private STM32L4_PWR pwr;

        private const long CR1 = 0x00;
        private const long CR3 = 0x08;
        private const long SR2 = 0x14;
        private const long CR5 = 0x80;

        private const uint DBP = 1u << 8;
        private const uint VOS_RANGE1 = 0b01u << 9;
        private const uint VOS_RANGE2 = 0b10u << 9;
        private const uint LPR = 1u << 14;
        private const uint REGLPF = 1u << 9;
        private const uint VOSF = 1u << 10;
    }
}
