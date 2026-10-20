//
// Copyright (c) 2026 Philipp Erhardt <philipp.erhardt@tum.de>
//
//  This file is licensed under the MIT License.
//  Full license text is available in 'licenses/MIT.txt'.
//
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Memory;

namespace Antmicro.Renode.Peripherals.MTD
{
    // STM32L4+ embedded flash interface. Register layout: RM0432 Rev 9 section 3.7 and ST's
    // stm32l4r5xx.h (FLASH_TypeDef, FLASH_*_Pos); keys from stm32l4xx_hal_flash.h.
    // Erases complete instantly (BSY never reads set). Programming happens through ordinary
    // bus writes to the flash memory, as the CPU does with PG=1.
    public class STM32L4_FlashController : STM32_FlashController, IKnownSize
    {
        public STM32L4_FlashController(IMachine machine, MappedMemory flash) : base(machine)
        {
            this.flash = flash;
            controlLock = new LockRegister(this, nameof(controlLock), ControlLockKey);
            optionLock = new LockRegister(this, nameof(optionLock), OptionLockKey);
            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            base.Reset();
            controlLock.Reset();
            optionLock.Reset();
        }

        public override void WriteDoubleWord(long offset, uint value)
        {
            if((Registers)offset == Registers.Control && controlLock.IsLocked)
            {
                this.Log(LogLevel.Warning, "Write to FLASH_CR while locked: 0x{0:X8}", value);
                return;
            }
            base.WriteDoubleWord(offset, value);
        }

        public long Size => 0x400;

        private void DefineRegisters()
        {
            // 3.7.1, reset 0x0000_0600 (ICEN, DCEN)
            Registers.AccessControl.Define(this, 0x00000600)
                .WithValueField(0, 4, name: "LATENCY")
                .WithReservedBits(4, 4)
                .WithFlag(8, name: "PRFTEN")
                .WithFlag(9, name: "ICEN")
                .WithFlag(10, name: "DCEN")
                .WithFlag(11, name: "ICRST")
                .WithFlag(12, name: "DCRST")
                .WithFlag(13, name: "RUN_PD")
                .WithFlag(14, name: "SLEEP_PD")
                .WithReservedBits(15, 17);

            Registers.PowerDownKey.Define(this).WithValueField(0, 32, FieldMode.Write, name: "PDKEYR");

            Registers.Key.Define(this)
                .WithValueField(0, 32, FieldMode.Write, writeCallback: (_, value) => controlLock.ConsumeValue((uint)value), name: "KEYR");

            Registers.OptionKey.Define(this)
                .WithValueField(0, 32, FieldMode.Write, writeCallback: (_, value) => optionLock.ConsumeValue((uint)value), name: "OPTKEYR");

            // 3.7.5: error and EOP flags are write-1-to-clear; nothing ever fails
            Registers.Status.Define(this)
                .WithFlag(0, FieldMode.Read | FieldMode.WriteOneToClear, name: "EOP")
                .WithFlag(1, FieldMode.Read | FieldMode.WriteOneToClear, name: "OPERR")
                .WithReservedBits(2, 1)
                .WithValueField(3, 7, FieldMode.Read | FieldMode.WriteOneToClear, name: "PROGERR..FASTERR")
                .WithReservedBits(10, 4)
                .WithFlag(14, FieldMode.Read | FieldMode.WriteOneToClear, name: "RDERR")
                .WithFlag(15, FieldMode.Read | FieldMode.WriteOneToClear, name: "OPTVERR")
                .WithFlag(16, FieldMode.Read, valueProviderCallback: _ => false, name: "BSY")
                .WithFlag(17, name: "PEMPTY")
                .WithReservedBits(18, 14);

            // 3.7.6, reset 0xC000_0000 (LOCK, OPTLOCK)
            Registers.Control.Define(this, 0xC0000000)
                .WithFlag(0, name: "PG")
                .WithFlag(1, out pageErase, name: "PER")
                .WithFlag(2, out massEraseBank1, name: "MER1")
                .WithValueField(3, 8, out pageNumber, name: "PNB")
                .WithFlag(11, out bankErase, name: "BKER")
                .WithReservedBits(12, 3)
                .WithFlag(15, out massEraseBank2, name: "MER2")
                .WithFlag(16, FieldMode.Write, writeCallback: (_, value) => { if(value) { StartErase(); } }, name: "STRT")
                .WithFlag(17, FieldMode.Write, writeCallback: (_, value) => { if(value) { this.Log(LogLevel.Warning, "Option byte programming is not supported"); } }, name: "OPTSTRT")
                .WithFlag(18, name: "FSTPG")
                .WithReservedBits(19, 5)
                .WithFlag(24, name: "EOPIE")
                .WithFlag(25, name: "ERRIE")
                .WithFlag(26, name: "RDERRIE")
                .WithFlag(27, FieldMode.Write, name: "OBL_LAUNCH")
                .WithReservedBits(28, 2)
                .WithFlag(30, FieldMode.Read | FieldMode.Set, valueProviderCallback: _ => optionLock.IsLocked,
                    writeCallback: (_, value) => { if(value) { optionLock.Lock(); } }, name: "OPTLOCK")
                .WithFlag(31, FieldMode.Read | FieldMode.Set, valueProviderCallback: _ => controlLock.IsLocked,
                    writeCallback: (_, value) => { if(value) { controlLock.Lock(); } }, name: "LOCK");

            Registers.Ecc.Define(this).WithValueField(0, 32, name: "ECCR");

            // 3.7.8, factory value 0xFFEF_F8AA: DBANK=1 (two banks of 4 KiB pages)
            Registers.Option.Define(this, 0xFFEFF8AA)
                .WithValueField(0, 32, out options, name: "OPTR");

            // 3.7.9-3.7.16: protection areas, factory defaults (stm32l4 PAC 0.16)
            Registers.Pcrop1Start.Define(this, 0xFFFFFFFF).WithValueField(0, 32, name: "PCROP1SR");
            Registers.Pcrop1End.Define(this, 0x0FF00000).WithValueField(0, 32, name: "PCROP1ER");
            Registers.WriteProtection1A.Define(this, 0xFF00FF00).WithValueField(0, 32, name: "WRP1AR");
            Registers.WriteProtection1B.Define(this, 0xFF00FF00).WithValueField(0, 32, name: "WRP1BR");
            Registers.Pcrop2Start.Define(this, 0xFFFFFFFF).WithValueField(0, 32, name: "PCROP2SR");
            Registers.Pcrop2End.Define(this, 0x0FF00000).WithValueField(0, 32, name: "PCROP2ER");
            Registers.WriteProtection2A.Define(this, 0xFF00FF00).WithValueField(0, 32, name: "WRP2AR");
            Registers.WriteProtection2B.Define(this, 0xFF00FF00).WithValueField(0, 32, name: "WRP2BR");
            Registers.Configuration.Define(this).WithValueField(0, 32, name: "CFGR");
        }

        private bool DualBank => (options.Value & DualBankOption) != 0;

        private long PageSize => DualBank ? DualBankPageSize : SingleBankPageSize;

        private long BankSize => DualBank ? flash.Size / 2 : flash.Size;

        private void StartErase()
        {
            if(pageErase.Value)
            {
                var bankOffset = DualBank && bankErase.Value ? BankSize : 0;
                var offset = bankOffset + (long)pageNumber.Value * PageSize;
                Erase(offset, PageSize);
            }
            if(massEraseBank1.Value)
            {
                Erase(0, BankSize);
            }
            if(massEraseBank2.Value && DualBank)
            {
                Erase(BankSize, BankSize);
            }
        }

        private void Erase(long offset, long size)
        {
            if(offset < 0 || offset + size > flash.Size)
            {
                this.Log(LogLevel.Error, "Erase of 0x{0:X} bytes at 0x{1:X} is outside flash", size, offset);
                return;
            }
            this.Log(LogLevel.Debug, "Erasing 0x{0:X} bytes at flash offset 0x{1:X}", size, offset);
            flash.WriteBytes(offset, Enumerable.Repeat(ErasedByte, (int)size).ToArray(), 0, (int)size);
        }

        private IFlagRegisterField pageErase;
        private IFlagRegisterField massEraseBank1;
        private IFlagRegisterField massEraseBank2;
        private IFlagRegisterField bankErase;
        private IValueRegisterField pageNumber;
        private IValueRegisterField options;

        private readonly MappedMemory flash;
        private readonly LockRegister controlLock;
        private readonly LockRegister optionLock;

        private const byte ErasedByte = 0xFF;
        private const ulong DualBankOption = 1UL << 22;
        private const long DualBankPageSize = 0x1000;
        private const long SingleBankPageSize = 0x2000;

        private static readonly uint[] ControlLockKey = { 0x45670123, 0xCDEF89AB };
        private static readonly uint[] OptionLockKey = { 0x08192A3B, 0x4C5D6E7F };

        // Offsets: stm32l4r5xx.h FLASH_TypeDef
        private enum Registers
        {
            AccessControl = 0x00,
            PowerDownKey = 0x04,
            Key = 0x08,
            OptionKey = 0x0C,
            Status = 0x10,
            Control = 0x14,
            Ecc = 0x18,
            Option = 0x20,
            Pcrop1Start = 0x24,
            Pcrop1End = 0x28,
            WriteProtection1A = 0x2C,
            WriteProtection1B = 0x30,
            Pcrop2Start = 0x44,
            Pcrop2End = 0x48,
            WriteProtection2A = 0x4C,
            WriteProtection2B = 0x50,
            Configuration = 0x130,
        }
    }
}
