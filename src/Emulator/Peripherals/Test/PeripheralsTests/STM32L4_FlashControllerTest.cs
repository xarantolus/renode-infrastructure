//
// Copyright (c) 2026 Philipp Erhardt <philipp.erhardt@tum.de>
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.MTD;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    // Offsets, bits and keys: RM0432 Rev 9 section 3.7, stm32l4r5xx.h and stm32l4xx_hal_flash.h
    [TestFixture]
    public class STM32L4_FlashControllerTest
    {
        [SetUp]
        public void SetUp()
        {
            var machine = new Machine();
            flash = new MappedMemory(machine, FlashSize);
            controller = new STM32L4_FlashController(machine, flash);
            for(long offset = 0; offset < FlashSize; offset += 4)
            {
                flash.WriteDoubleWord(offset, Pattern);
            }
        }

        [Test]
        public void ShouldResetLockedAndDualBank()
        {
            Assert.AreEqual(LOCK | OPTLOCK, controller.ReadDoubleWord(CR));
            Assert.AreEqual(0xFFEFF8AA, controller.ReadDoubleWord(OPTR));
            Assert.AreEqual(DBANK, controller.ReadDoubleWord(OPTR) & DBANK);
        }

        [Test]
        public void ShouldUnlockWithKeySequence()
        {
            Unlock();

            Assert.AreEqual(0u, controller.ReadDoubleWord(CR) & LOCK);
        }

        [Test]
        public void ShouldStayLockedAfterWrongKey()
        {
            controller.WriteDoubleWord(KEYR, Key1);
            controller.WriteDoubleWord(KEYR, 0x12345678);
            controller.WriteDoubleWord(KEYR, Key1);
            controller.WriteDoubleWord(KEYR, Key2);

            Assert.AreEqual(LOCK, controller.ReadDoubleWord(CR) & LOCK);
        }

        [Test]
        public void ShouldIgnoreEraseWhileLocked()
        {
            controller.WriteDoubleWord(CR, LOCK | OPTLOCK | PER | STRT);

            Assert.AreEqual(Pattern, flash.ReadDoubleWord(0));
        }

        [Test]
        public void ShouldErasePageInBank1()
        {
            Unlock();
            ErasePage(bank2: false, page: 3);

            AssertErased(3 * PageSize, PageSize);
            Assert.AreEqual(Pattern, flash.ReadDoubleWord(2 * PageSize));
            Assert.AreEqual(Pattern, flash.ReadDoubleWord(4 * PageSize));
            Assert.AreEqual(0u, controller.ReadDoubleWord(SR) & BSY);
        }

        [Test]
        public void ShouldErasePageInBank2()
        {
            // 0x1FC000 holds the MOVE-IIIa config storage partition: bank 2, page 252
            Unlock();
            ErasePage(bank2: true, page: 252);

            AssertErased(0x1FC000, PageSize);
            Assert.AreEqual(Pattern, flash.ReadDoubleWord(0xFC000));
        }

        [Test]
        public void ShouldMassEraseOneBank()
        {
            Unlock();
            controller.WriteDoubleWord(CR, MER2);
            controller.WriteDoubleWord(CR, MER2 | STRT);

            AssertErased(FlashSize / 2, FlashSize / 2);
            Assert.AreEqual(Pattern, flash.ReadDoubleWord(FlashSize / 2 - 4));
        }

        private void Unlock()
        {
            controller.WriteDoubleWord(KEYR, Key1);
            controller.WriteDoubleWord(KEYR, Key2);
        }

        private void ErasePage(bool bank2, uint page)
        {
            var cr = PER | (page << PNB_POS) | (bank2 ? BKER : 0);
            controller.WriteDoubleWord(CR, cr);
            controller.WriteDoubleWord(CR, cr | STRT);
        }

        private void AssertErased(long offset, long size)
        {
            for(var address = offset; address < offset + size; address += 4)
            {
                Assert.AreEqual(0xFFFFFFFF, flash.ReadDoubleWord(address), $"0x{address:X}");
            }
        }

        private MappedMemory flash;
        private STM32L4_FlashController controller;

        private const long FlashSize = 0x200000;
        private const long PageSize = 0x1000;
        private const uint Pattern = 0xA5A5A5A5;
        private const uint Key1 = 0x45670123;
        private const uint Key2 = 0xCDEF89AB;

        private const long KEYR = 0x08;
        private const long SR = 0x10;
        private const long CR = 0x14;
        private const long OPTR = 0x20;

        private const uint PER = 1u << 1;
        private const int PNB_POS = 3;
        private const uint BKER = 1u << 11;
        private const uint MER2 = 1u << 15;
        private const uint STRT = 1u << 16;
        private const uint OPTLOCK = 1u << 30;
        private const uint LOCK = 1u << 31;
        private const uint BSY = 1u << 16;
        private const uint DBANK = 1u << 22;
    }
}
