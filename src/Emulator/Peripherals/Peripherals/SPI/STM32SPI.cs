//
// Copyright (c) 2010-2024 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities.Collections;

namespace Antmicro.Renode.Peripherals.SPI
{
    public sealed class STM32SPI : NullRegistrationPointPeripheralContainer<ISPIPeripheral>, IWordPeripheral, IDoubleWordPeripheral, IBytePeripheral, IKnownSize
    {
        public STM32SPI(IMachine machine, STM32Series series, int bufferCapacity = DefaultBufferCapacity) : base(machine)
        {
            var supportedSeries = new List<STM32Series>{
                STM32Series.F0,
                STM32Series.F1,
                STM32Series.F4,
                STM32Series.F7,
                STM32Series.G0,
                STM32Series.L0,
                STM32Series.L1,
                STM32Series.L4,
                STM32Series.L5,
            };
            if(!supportedSeries.Contains(series))
            {
                throw new ConstructionException($"Unsupported STM32 series value: {series}!");
            }
            this.series = series;

            receiveBuffer = new CircularBuffer<byte>(bufferCapacity);
            IRQ = new GPIO();
            DMAReceive = new GPIO();
            DMASend = new GPIO();
            registers = new DoubleWordRegisterCollection(this);
            SetupRegisters();
            Reset();
        }

        // We can't use AllowedTranslations because then WriteByte/WriteWord will trigger
        // an additional read (see ReadWriteExtensions:WriteByteUsingDoubleWord).
        // We can't have this happen for the data register.
        // The access width of SPI_DR matters on series with a data-size field: a byte access moves
        // one byte of the FIFO, a half-word access moves two (data packing).
        public byte ReadByte(long offset)
        {
            // byte interface is there for DMA
            if(offset == (long)Registers.Data)
            {
                return (byte)HandleDataRead(1);
            }
            if(offset % 4 == 0)
            {
                return (byte)ReadDoubleWord(offset);
            }
            this.LogUnhandledRead(offset);
            return 0;
        }

        public void WriteByte(long offset, byte value)
        {
            if(offset == (long)Registers.Data)
            {
                HandleDataWrite(value, 1);
            }
            else if(offset % 4 == 0)
            {
                WriteDoubleWord(offset, (uint)value);
            }
            else
            {
                this.LogUnhandledWrite(offset, value);
            }
        }

        public ushort ReadWord(long offset)
        {
            if(offset == (long)Registers.Data)
            {
                return (ushort)HandleDataRead(2);
            }
            return (ushort)ReadDoubleWord(offset);
        }

        public void WriteWord(long offset, ushort value)
        {
            if(offset == (long)Registers.Data)
            {
                HandleDataWrite(value, 2);
                return;
            }
            WriteDoubleWord(offset, (uint)value);
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset == (long)Registers.Data)
            {
                return HandleDataRead(2);
            }
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset == (long)Registers.Data)
            {
                HandleDataWrite(value, 2);
                return;
            }
            registers.Write(offset, value);
        }

        public override void Reset()
        {
            IRQ.Unset();
            DMAReceive.Unset();
            DMASend.Unset();
            lock(receiveBuffer)
            {
                receiveBuffer.Clear();
            }
            registers.Reset();
        }

        public long Size
        {
            get
            {
                return 0x400;
            }
        }

        public GPIO IRQ { get; }

        public GPIO DMAReceive { get; }

        public GPIO DMASend { get; }

        // Returns up to `accessBytes` bytes from the RX FIFO, the oldest in the least significant byte.
        private uint HandleDataRead(int accessBytes)
        {
            IRQ.Unset();
            lock(receiveBuffer)
            {
                var count = HasDataSize ? accessBytes : 1;
                uint value = 0;
                for(var i = 0; i < count; i++)
                {
                    // We don't warn when the data register is read while it's empty because the HAL
                    // (for example L0, F4) does this intentionally.
                    // See https://github.com/STMicroelectronics/STM32CubeL0/blob/bec4e499a74de98ab60784bf2ef1912bee9c1a22/Drivers/STM32L0xx_HAL_Driver/Src/stm32l0xx_hal_spi.c#L1368-L1372
                    if(!receiveBuffer.TryDequeue(out var b))
                    {
                        break;
                    }
                    value |= (uint)b << (8 * i);
                }
                Update();
                return value;
            }
        }

        // Frames wider than 8 bits are shifted out most significant byte first. With frames of
        // 8 bits or less, a half-word access sends two frames, least significant byte first
        // (RM0432 Rev 9 section 52, "Data packing").
        private void HandleDataWrite(uint value, int accessBytes)
        {
            IRQ.Unset();
            DMASend.Set(false);
            lock(receiveBuffer)
            {
                if(HasDataSize && FrameBits > 8)
                {
                    var frame = value & ((1u << FrameBits) - 1);
                    var high = TransmitByte((byte)(frame >> 8));
                    var low = TransmitByte((byte)frame);
                    EnqueueResponse(low);
                    EnqueueResponse(high);
                    this.NoisyLog("Transmitted frame 0x{0:X}, received 0x{1:X}.", frame, (high << 8) | low);
                }
                else
                {
                    var frames = HasDataSize ? accessBytes : 1;
                    for(var i = 0; i < frames; i++)
                    {
                        var data = (byte)(value >> (8 * i));
                        var response = TransmitByte(data);
                        EnqueueResponse(response);
                        this.NoisyLog("Transmitted 0x{0:X}, received 0x{1:X}.", data, response);
                    }
                }
            }
            if(txDmaEnable.Value)
            {
                DMASend.Set(true);
            }
            Update();
        }

        private byte TransmitByte(byte data)
        {
            var peripheral = RegisteredPeripheral;
            if(peripheral == null)
            {
                this.Log(LogLevel.Warning, "SPI transmission while no SPI peripheral is connected.");
                return 0;
            }
            return peripheral.Transmit(data);
        }

        private void EnqueueResponse(byte response)
        {
            if(receiveBuffer.Count == receiveBuffer.Capacity)
            {
                this.Log(LogLevel.Debug, "Receiving response while RXFIFO is full, dropping it");
                overrun.Value = true;
            }
            else
            {
                receiveBuffer.Enqueue(response);
            }
            if(rxDmaEnable.Value)
            {
                // This blink is used to signal the DMA that it should perform the peripheral -> memory transaction now
                // Without this signal DMA will never move data from the receive buffer to memory
                // See STM32DMA:OnGPIO
                DMAReceive.Blink();
            }
        }

        private void Update()
        {
            var rxBufferNotEmptyInterruptFlag = IsRxBufferNotEmpty() && rxBufferNotEmptyInterruptEnable.Value;

            // Consider only the OVR event flag as the MODF (Master Mode fault event), FRE (TI frame
            // format error) and CRCERR (CRC protocol error) are not supported by this model.
            var errorInterrupt = errorInterruptEnable.Value && overrun.Value;

            IRQ.Set(txBufferEmptyInterruptEnable.Value || rxBufferNotEmptyInterruptFlag || errorInterrupt);
        }

        private bool IsRxBufferNotEmpty()
        {
            if(HasDataSize && !fifoReceptionThreshold.Value)
            {
                return receiveBuffer.Count >= 2;
            }

            return receiveBuffer.Count != 0;
        }

        private void SetupRegisters()
        {
            // Some fields relate to the physical layer of the SPI protocol, these are not
            // taken into account in Renode and are defined as flags or value fields without
            // a corresponding RegisterField or read/write callbacks. They are marked with
            // comments.
            Registers.Control1.Define(registers)
                .WithFlag(0, name: "CPHA") // Physical
                .WithFlag(1, name: "CPOL") // Physical
                .WithFlag(2, out masterMode, writeCallback: (old_value, value) =>
                {
                    if(!value && old_value)
                    {
                        this.Log(LogLevel.Error, "Setting slave mode which is not supported.");
                    }
                }, name: "MSTR")
                .WithValueField(3, 3, name: "Baud") // Physical
                .WithFlag(6, changeCallback: (oldValue, newValue) =>
                {
                    if(!newValue)
                    {
                        IRQ.Unset();
                    }
                    else
                    {
                        if(!masterMode.Value)
                        {
                            this.Log(LogLevel.Error, "Enabled SPI in slave mode, which is not supported.");
                        }
                        DMASend.Set(txDmaEnable.Value);
                    }
                }, name: "SpiEnable")
                .WithFlag(7, name: "LSBFIRST") // Physical
                                               // We keep these as flags to preserve written values. SSI flag is used by drivers to select/detect operation mode (Master or Slave)
                .WithFlag(8, name: "SSI") // Internal slave select
                .WithFlag(9, name: "SSM") // Software slave management
                .WithTaggedFlag("RXONLY", 10)
                .WithTaggedFlag("DFF", 11)
                .WithTaggedFlag("CRCNEXT", 12)
                .WithTaggedFlag("CRCEN", 13)
                .WithTaggedFlag("BIDIOE", 14)
                .WithTaggedFlag("BIDIMODE", 15);

            // SPIx_CR2 resets to 0x0700 (DS = 8 bit) where the data-size field exists (RM0432 Rev 9 section 52.6.2)
            Registers.Control2.Define(registers, HasDataSize ? 0x0700u : 0u)
                .WithFlag(0, out rxDmaEnable, name: "RXDMAEN")
                .WithFlag(1, out txDmaEnable, writeCallback: (_, value) =>
                {
                    DMASend.Set(value);
                }, name: "TXDMAEN")
                .WithTaggedFlag("SSOE", 2)
                .If(HasDataSize)
                    .Then(reg => reg
                          .WithFlag(3, name: "NSSP") // Physical
                    )
                    .Else(reg => reg
                          .WithReservedBits(3, 1)
                    )
                .WithTaggedFlag("FRF", 4)
                .WithFlag(5, out errorInterruptEnable, name: "ERRIE")
                .WithFlag(6, out rxBufferNotEmptyInterruptEnable, name: "RXNEIE")
                .WithFlag(7, out txBufferEmptyInterruptEnable, name: "TXEIE")
                .If(HasDataSize)
                    .Then(reg => reg
                          .WithValueField(8, 4, out dataSize, name: "DS")
                          .WithFlag(12, out fifoReceptionThreshold, name: "FRXTH")
                    )
                    .Else(reg => reg
                          .WithReservedBits(8, 4)
                          .WithReservedBits(12, 1)
                    )
                .WithReservedBits(13, 19)
                .WithWriteCallback((_, __) => Update());

            Registers.Status.Define(registers, 2)
                .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => IsRxBufferNotEmpty(), name: "RXNE")
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => true, name: "TXE") // transfers are instant
                .WithTaggedFlag("CHSIDE", 2) // r/o
                .WithTaggedFlag("UDR", 3) // r/o
                .WithTaggedFlag("CRCERR", 4) // rc_w0
                .WithTaggedFlag("MODF", 5) // r/o
                .WithFlag(6, out overrun, FieldMode.Read, readCallback: (_, __) =>
                {
                    if(receiveBuffer.Count < receiveBuffer.Capacity)
                    {
                        overrun.Value = false;
                    }
                }, name: "OVR")
                .WithTaggedFlag("BSY", 7) // r/o
                .WithTaggedFlag("FRE", 8) // r/o
                .WithReservedBits(9, 23);


            Registers.CRCPolynomial.Define(registers, 7)
                .WithTag("CRCPOLY", 0, 16)
                .WithReservedBits(16, 16);

            Registers.ReceivedCRC.Define(registers)
                .WithTag("RxCRC", 0, 16) // r/o
                .WithReservedBits(16, 16);

            Registers.TransmittedCRC.Define(registers)
                .WithTag("TxCRC", 0, 16) // r/o
                .WithReservedBits(16, 16);

            Registers.I2SConfiguration.Define(registers)
                .WithTaggedFlag("CHLEN", 0)
                .WithTag("DATLEN", 1, 2)
                .WithTaggedFlag("CKPOL", 3)
                .WithTag("I2SSTD", 4, 2)
                .WithReservedBits(6, 1)
                .WithTaggedFlag("PCMSYNC", 7)
                .WithTag("I2SCFG", 8, 2)
                .WithFlag(10, FieldMode.Read | FieldMode.WriteOneToClear, writeCallback: (oldValue, newValue) =>
                {
                    // write one to clear to keep this bit 0
                    if(newValue)
                    {
                        this.Log(LogLevel.Warning, "Trying to enable not supported I2S mode.");
                    }
                }, name: "I2SE")
                .WithTaggedFlag("I2SMOD", 11)
                .WithReservedBits(12, 20);

            Registers.I2SPrescaler.Define(registers, 2)
                .WithTag("I2SDIV", 0, 8)
                .WithTaggedFlag("ODD", 8)
                .WithTaggedFlag("MCKOE", 9)
                .WithReservedBits(10, 22);
        }

        private IFlagRegisterField txBufferEmptyInterruptEnable, rxBufferNotEmptyInterruptEnable, rxDmaEnable;
        private IFlagRegisterField txDmaEnable;
        private IFlagRegisterField masterMode;
        private IFlagRegisterField overrun;
        private IFlagRegisterField errorInterruptEnable;
        // Fields of series with a data-size field (L4, L5)
        private IFlagRegisterField fifoReceptionThreshold;
        private IValueRegisterField dataSize;

        private readonly DoubleWordRegisterCollection registers;

        private readonly CircularBuffer<byte> receiveBuffer;

        // L4/L4+ and L5 SPIs share the FIFO/data-size layout (stm32l4r5xx.h SPI_CR2 DS, FRXTH, NSSP)
        private bool HasDataSize => series == STM32Series.L5 || series == STM32Series.L4;

        // DS = 0b0011..0b1111 selects 4..16 bit frames; 0b0000..0b0010 are not allowed and map to 8 bit
        private int FrameBits => dataSize.Value < 0b0011 ? 8 : (int)dataSize.Value + 1;

        private readonly STM32Series series;

        private const int DefaultBufferCapacity = 4;

        private enum Registers
        {
            Control1 = 0x0, // SPI_CR1,
            Control2 = 0x4, // SPI_CR2
            Status = 0x8, // SPI_SR
            Data = 0xC, // SPI_DR
            CRCPolynomial = 0x10, // SPI_CRCPR
            ReceivedCRC = 0x14, // SPI_RXCRCR
            TransmittedCRC = 0x18, // SPI_TXCRCR
            I2SConfiguration = 0x1C, // SPI_I2SCFGR
            I2SPrescaler = 0x20, // SPI_I2SPR
        }
    }
}