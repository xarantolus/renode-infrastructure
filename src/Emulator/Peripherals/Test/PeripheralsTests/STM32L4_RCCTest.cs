//
// Copyright (c) 2026 Philipp Erhardt <philipp.erhardt@tum.de>
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Miscellaneous;

using Moq;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    // Offsets, bits and reset values: RM0432 Rev 9 section 6.4 and stm32l4r5xx.h
    [TestFixture]
    public class STM32L4_RCCTest
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            systick = new Mock<IHasDivisibleFrequency>();
            systick.SetupProperty(s => s.Frequency);
            rcc = new STM32L4_RCC(machine, systick.Object);
        }

        [Test]
        public void ShouldResetToMsi4MHz()
        {
            Assert.AreEqual(0x00000063, rcc.ReadDoubleWord(CR));
            Assert.AreEqual(0x00001000, rcc.ReadDoubleWord(PLLCFGR));
            Assert.AreEqual(0x0C000600, rcc.ReadDoubleWord(CSR));
            Assert.AreEqual(4000000, systick.Object.Frequency);
        }

        [Test]
        public void ShouldReportOscillatorsReadyOnceEnabled()
        {
            rcc.WriteDoubleWord(CR, rcc.ReadDoubleWord(CR) | HSION | HSEON | PLLON);
            var cr = rcc.ReadDoubleWord(CR);
            Assert.AreEqual(HSIRDY, cr & HSIRDY);
            Assert.AreEqual(HSERDY, cr & HSERDY);
            Assert.AreEqual(PLLRDY, cr & PLLRDY);

            rcc.WriteDoubleWord(CRRCR, HSI48ON);
            Assert.AreEqual(HSI48RDY, rcc.ReadDoubleWord(CRRCR) & HSI48RDY);

            rcc.WriteDoubleWord(BDCR, LSEON);
            Assert.AreEqual(LSERDY, rcc.ReadDoubleWord(BDCR) & LSERDY);
        }

        [TestCase(40u, 80000000UL)]
        [TestCase(60u, 120000000UL)]
        public void ShouldClockSysTickFromPllR(uint plln, ulong expected)
        {
            // MSI 4 MHz -> PLLM /1 -> xPLLN -> PLLR /2, as configured by the MOVE-IIIa boards
            rcc.WriteDoubleWord(PLLCFGR, PLLSRC_MSI | (plln << PLLN_POS) | PLLREN);
            rcc.WriteDoubleWord(CR, rcc.ReadDoubleWord(CR) | PLLON);
            rcc.WriteDoubleWord(CFGR, SW_PLL);

            Assert.AreEqual(SW_PLL << SWS_POS, rcc.ReadDoubleWord(CFGR) & SWS_MASK);
            Assert.AreEqual(expected, systick.Object.Frequency);
        }

        [Test]
        public void ShouldApplyPllMAndPllRDividers()
        {
            // 16 MHz HSI16 / PLLM 4 * PLLN 40 / PLLR 8 = 20 MHz
            rcc.WriteDoubleWord(PLLCFGR, PLLSRC_HSI16 | (3u << PLLM_POS) | (40u << PLLN_POS) | (3u << PLLR_POS));
            rcc.WriteDoubleWord(CFGR, SW_PLL);

            Assert.AreEqual(20000000, systick.Object.Frequency);
        }

        [TestCase(8u, 16000000UL)]
        [TestCase(11u, 48000000UL)]
        public void ShouldUseMsiRangeWhileMsiIsRunning(uint range, ulong expected)
        {
            // MSIRANGE may change while MSI is on and ready (RM0432 6.4.1)
            rcc.WriteDoubleWord(CR, MSION | MSIRGSEL | (range << MSIRANGE_POS));

            Assert.AreEqual(expected, systick.Object.Frequency);
        }

        [TestCase(0b1000u, 2UL)]
        [TestCase(0b1011u, 16UL)]
        [TestCase(0b1100u, 64UL)]
        [TestCase(0b1111u, 512UL)]
        public void ShouldDivideHclkByAhbPrescaler(uint hpre, ulong divider)
        {
            rcc.WriteDoubleWord(CFGR, hpre << HPRE_POS);

            Assert.AreEqual(4000000 / divider, systick.Object.Frequency);
        }

        [Test]
        public void ShouldClearResetFlagsOnRmvf()
        {
            rcc.WriteDoubleWord(CSR, rcc.ReadDoubleWord(CSR) | RMVF);

            Assert.AreEqual(0u, rcc.ReadDoubleWord(CSR) & RESET_FLAGS);
        }

        [Test]
        public void ShouldKeepPeripheralEnableBits()
        {
            // GPIOFEN/GPIOGEN/GPIOIEN exist on STM32L4+ only
            const uint gpioFGI = (1u << 5) | (1u << 6) | (1u << 8);
            rcc.WriteDoubleWord(AHB2ENR, gpioFGI);

            Assert.AreEqual(gpioFGI, rcc.ReadDoubleWord(AHB2ENR));
        }

        private Machine machine;
        private Mock<IHasDivisibleFrequency> systick;
        private STM32L4_RCC rcc;

        private const long CR = 0x00;
        private const long CFGR = 0x08;
        private const long PLLCFGR = 0x0C;
        private const long AHB2ENR = 0x4C;
        private const long BDCR = 0x90;
        private const long CSR = 0x94;
        private const long CRRCR = 0x98;

        private const uint MSION = 1u << 0;
        private const uint MSIRGSEL = 1u << 3;
        private const int MSIRANGE_POS = 4;
        private const uint HSION = 1u << 8;
        private const uint HSIRDY = 1u << 10;
        private const uint HSEON = 1u << 16;
        private const uint HSERDY = 1u << 17;
        private const uint PLLON = 1u << 24;
        private const uint PLLRDY = 1u << 25;

        private const uint SW_PLL = 0b11;
        private const int SWS_POS = 2;
        private const uint SWS_MASK = 0b11u << SWS_POS;
        private const int HPRE_POS = 4;

        private const uint PLLSRC_MSI = 0b01;
        private const uint PLLSRC_HSI16 = 0b10;
        private const int PLLM_POS = 4;
        private const int PLLN_POS = 8;
        private const uint PLLREN = 1u << 24;
        private const int PLLR_POS = 25;

        private const uint LSEON = 1u << 0;
        private const uint LSERDY = 1u << 1;
        private const uint RMVF = 1u << 23;
        private const uint RESET_FLAGS = 0xFFu << 24;
        private const uint HSI48ON = 1u << 0;
        private const uint HSI48RDY = 1u << 1;
    }
}
