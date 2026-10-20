//
// Copyright (c) 2026 Philipp Erhardt <philipp.erhardt@tum.de>
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.SPI;

using Moq;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    // SPI_CR2 DS/FRXTH and data packing: RM0432 Rev 9 section 52 and stm32l4r5xx.h
    [TestFixture]
    public class STM32SPI_DataSizeTest
    {
        [SetUp]
        public void SetUp()
        {
            sent = new List<byte>();
            var peer = new Mock<ISPIPeripheral>();
            // Answers each byte with its complement so the received frame is distinguishable
            peer.Setup(p => p.Transmit(It.IsAny<byte>())).Returns<byte>(b => { sent.Add(b); return (byte)~b; });
            var machine = new Machine();
            spi = new STM32SPI(machine, STM32Series.L4);
            machine.SystemBus.Register(spi, new BusRangeRegistration(new Core.Range(SpiAddress, (ulong)spi.Size)));
            spi.Register(peer.Object, NullRegistrationPoint.Instance);
        }

        [Test]
        public void ShouldResetCr2ToEightBitFrames()
        {
            Assert.AreEqual(0x0700u, spi.ReadDoubleWord(CR2));
        }

        [Test]
        public void ShouldSendSixteenBitFrameMostSignificantByteFirst()
        {
            spi.WriteDoubleWord(CR2, DS_16BIT);
            spi.WriteWord(DR, 0x4079);

            CollectionAssert.AreEqual(new byte[] { 0x40, 0x79 }, sent);
            Assert.AreEqual((ushort)0xBF86, spi.ReadWord(DR));
        }

        [Test]
        public void ShouldPackTwoEightBitFramesInHalfWordAccess()
        {
            spi.WriteWord(DR, 0x3412);

            CollectionAssert.AreEqual(new byte[] { 0x12, 0x34 }, sent);
            Assert.AreEqual((ushort)0xCBED, spi.ReadWord(DR));
        }

        [Test]
        public void ShouldSendOneFrameForByteAccess()
        {
            spi.WriteByte(DR, 0xA2);

            CollectionAssert.AreEqual(new byte[] { 0xA2 }, sent);
            Assert.AreEqual((byte)0x5D, spi.ReadByte(DR));
        }

        [Test]
        public void ShouldSignalRxneForOneByteOnlyWithQuarterThreshold()
        {
            spi.WriteByte(DR, 0x01);
            Assert.AreEqual(0u, spi.ReadDoubleWord(SR) & RXNE);

            spi.WriteDoubleWord(CR2, spi.ReadDoubleWord(CR2) | FRXTH);
            Assert.AreEqual(RXNE, spi.ReadDoubleWord(SR) & RXNE);
        }

        private List<byte> sent;
        private STM32SPI spi;

        // SPI1_BASE
        private const ulong SpiAddress = 0x40013000;
        private const long CR2 = 0x04;
        private const long SR = 0x08;
        private const long DR = 0x0C;

        private const uint DS_16BIT = 0b1111u << 8;
        private const uint FRXTH = 1u << 12;
        private const uint RXNE = 1u << 0;
    }
}
