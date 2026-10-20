//
// Copyright (c) 2025 Philippe Michaud-Boudreault <philmb3487@proton.me>
// Copyright (c) 2010-2024 Antmicro
//
//  This file is licensed under the MIT License.
//  Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // STM32L4+ (L4Rx/L4Sx) reset and clock control.
    // Register layout: RM0432 Rev 9 section 6.4 and ST's stm32l4r5xx.h (RCC_TypeDef, RCC_*_Pos).
    // Oscillators become ready as soon as they are switched on. Only SYSCLK/HCLK is propagated
    // (to the SysTick); peripheral enable/reset bits are plain storage.
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class STM32L4_RCC : BasicDoubleWordPeripheral, IKnownSize
    {
        public STM32L4_RCC(IMachine machine, IHasDivisibleFrequency systick = null, IPeripheral rtc = null,
            ulong hseFrequency = DefaultHseFrequency) : base(machine)
        {
            this.systick = systick;
            this.rtc = rtc;
            this.hseFrequency = hseFrequency;
            if(systick == null)
            {
                this.Log(LogLevel.Warning, "No systick passed; system clock changes will be ignored");
            }
            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            base.Reset();
            UpdateClocks();
        }

        public long Size => 0x400;

        public ulong SystemClockFrequency
        {
            get
            {
                switch(systemClockSwitch.Value)
                {
                case SystemClockSource.Msi:
                    return MsiFrequency;
                case SystemClockSource.Hsi16:
                    return Hsi16Frequency;
                case SystemClockSource.Hse:
                    return hseFrequency;
                default:
                    return PllRFrequency;
                }
            }
        }

        public ulong HclkFrequency => SystemClockFrequency / AhbDivider(ahbPrescaler.Value);

        private void UpdateClocks()
        {
            if(systick == null)
            {
                return;
            }
            var hclk = HclkFrequency;
            if(hclk == 0 || systick.Frequency == hclk)
            {
                return;
            }
            systick.Frequency = hclk;
            this.Log(LogLevel.Debug, "HCLK changed to {0} Hz", hclk);
        }

        private void DefineRegisters()
        {
            // RM0432 6.4.1, reset 0x0000_0063 (MSION, MSIRDY, MSIRANGE=6)
            Registers.ClockControl.Define(this, 0x00000063)
                .WithFlag(0, out var msiOn, name: "MSION")
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => msiOn.Value, name: "MSIRDY")
                .WithFlag(2, name: "MSIPLLEN")
                .WithFlag(3, out msiRangeSelect, name: "MSIRGSEL")
                .WithValueField(4, 4, out msiRange, name: "MSIRANGE")
                .WithFlag(8, out var hsiOn, name: "HSION")
                .WithFlag(9, name: "HSIKERON")
                .WithFlag(10, FieldMode.Read, valueProviderCallback: _ => hsiOn.Value, name: "HSIRDY")
                .WithFlag(11, name: "HSIASFS")
                .WithReservedBits(12, 4)
                .WithFlag(16, out var hseOn, name: "HSEON")
                .WithFlag(17, FieldMode.Read, valueProviderCallback: _ => hseOn.Value, name: "HSERDY")
                .WithFlag(18, name: "HSEBYP")
                .WithFlag(19, name: "CSSON")
                .WithReservedBits(20, 4)
                .WithFlag(24, out var pllOn, name: "PLLON")
                .WithFlag(25, FieldMode.Read, valueProviderCallback: _ => pllOn.Value, name: "PLLRDY")
                .WithFlag(26, out var pllSai1On, name: "PLLSAI1ON")
                .WithFlag(27, FieldMode.Read, valueProviderCallback: _ => pllSai1On.Value, name: "PLLSAI1RDY")
                .WithFlag(28, out var pllSai2On, name: "PLLSAI2ON")
                .WithFlag(29, FieldMode.Read, valueProviderCallback: _ => pllSai2On.Value, name: "PLLSAI2RDY")
                .WithReservedBits(30, 2)
                .WithWriteCallback((_, __) => UpdateClocks());

            // 6.4.2; calibration values only
            Registers.InternalClockSourcesCalibration.Define(this, 0x40000000)
                .WithValueField(0, 32, name: "ICSCR");

            // 6.4.3, reset 0
            Registers.ClockConfiguration.Define(this)
                .WithEnumField(0, 2, out systemClockSwitch, name: "SW")
                .WithValueField(2, 2, FieldMode.Read, valueProviderCallback: _ => (ulong)systemClockSwitch.Value, name: "SWS")
                .WithValueField(4, 4, out ahbPrescaler, name: "HPRE")
                .WithValueField(8, 3, name: "PPRE1")
                .WithValueField(11, 3, name: "PPRE2")
                .WithReservedBits(14, 1)
                .WithFlag(15, name: "STOPWUCK")
                .WithReservedBits(16, 8)
                .WithValueField(24, 4, name: "MCOSEL")
                .WithValueField(28, 3, name: "MCOPRE")
                .WithReservedBits(31, 1)
                .WithWriteCallback((_, __) => UpdateClocks());

            // 6.4.4, reset 0x0000_1000. PLLCLK = input * PLLN / PLLM / PLLR
            Registers.PllConfiguration.Define(this, 0x00001000)
                .WithEnumField(0, 2, out pllSource, name: "PLLSRC")
                .WithReservedBits(2, 2)
                .WithValueField(4, 4, out pllM, name: "PLLM")
                .WithValueField(8, 7, out pllN, name: "PLLN")
                .WithReservedBits(15, 1)
                .WithFlag(16, name: "PLLPEN")
                .WithFlag(17, name: "PLLP")
                .WithReservedBits(18, 2)
                .WithFlag(20, name: "PLLQEN")
                .WithValueField(21, 2, name: "PLLQ")
                .WithReservedBits(23, 1)
                .WithFlag(24, name: "PLLREN")
                .WithValueField(25, 2, out pllR, name: "PLLR")
                .WithValueField(27, 5, name: "PLLPDIV")
                .WithWriteCallback((_, __) => UpdateClocks());

            // 6.4.5/6.4.6: PLLSAI1/2 outputs do not feed any modelled clock
            Registers.PllSai1Configuration.Define(this, 0x00001000).WithValueField(0, 32, name: "PLLSAI1CFGR");
            Registers.PllSai2Configuration.Define(this, 0x00001000).WithValueField(0, 32, name: "PLLSAI2CFGR");

            // 6.4.7-6.4.9: no clock-ready interrupts are raised, so flags stay clear
            Registers.ClockInterruptEnable.Define(this).WithValueField(0, 11, name: "CIER").WithReservedBits(11, 21);
            Registers.ClockInterruptFlag.Define(this).WithValueField(0, 11, FieldMode.Read, valueProviderCallback: _ => 0, name: "CIFR").WithReservedBits(11, 21);
            Registers.ClockInterruptClear.Define(this).WithValueField(0, 11, FieldMode.Write, name: "CICR").WithReservedBits(11, 21);

            // 6.4.10-6.4.27: reset and clock gating; reset values from RM0432 where the
            // manual states them, otherwise from the STM32L4R5 SVD (stm32l4 PAC 0.16)
            Registers.Ahb1Reset.Define(this).WithValueField(0, 32, name: "AHB1RSTR");
            Registers.Ahb2Reset.Define(this).WithValueField(0, 32, name: "AHB2RSTR");
            Registers.Ahb3Reset.Define(this).WithValueField(0, 32, name: "AHB3RSTR");
            Registers.Apb1Reset1.Define(this).WithValueField(0, 32, name: "APB1RSTR1");
            Registers.Apb1Reset2.Define(this).WithValueField(0, 32, name: "APB1RSTR2");
            Registers.Apb2Reset.Define(this).WithValueField(0, 32, name: "APB2RSTR");
            Registers.Ahb1Enable.Define(this, 0x00000100).WithValueField(0, 32, name: "AHB1ENR");
            Registers.Ahb2Enable.Define(this).WithValueField(0, 32, name: "AHB2ENR");
            Registers.Ahb3Enable.Define(this).WithValueField(0, 32, name: "AHB3ENR");
            Registers.Apb1Enable1.Define(this).WithValueField(0, 32, name: "APB1ENR1");
            Registers.Apb1Enable2.Define(this).WithValueField(0, 32, name: "APB1ENR2");
            Registers.Apb2Enable.Define(this).WithValueField(0, 32, name: "APB2ENR");
            Registers.Ahb1SleepEnable.Define(this, 0x00011303).WithValueField(0, 32, name: "AHB1SMENR");
            Registers.Ahb2SleepEnable.Define(this, 0x005777FF).WithValueField(0, 32, name: "AHB2SMENR");
            Registers.Ahb3SleepEnable.Define(this, 0x00000301).WithValueField(0, 32, name: "AHB3SMENR");
            Registers.Apb1SleepEnable1.Define(this, 0xF2FECA3F).WithValueField(0, 32, name: "APB1SMENR1");
            Registers.Apb1SleepEnable2.Define(this, 0x00000023).WithValueField(0, 32, name: "APB1SMENR2");
            Registers.Apb2SleepEnable.Define(this, 0x0D677801).WithValueField(0, 32, name: "APB2SMENR");
            Registers.IndependentClockConfiguration.Define(this).WithValueField(0, 32, name: "CCIPR");
            Registers.IndependentClockConfiguration2.Define(this).WithValueField(0, 32, name: "CCIPR2");
            Registers.OctospiDelayConfiguration.Define(this).WithValueField(0, 32, name: "DLYCFGR");

            // 6.4.29, reset 0 (backup domain)
            Registers.BackupDomainControl.Define(this)
                .WithFlag(0, out var lseOn, name: "LSEON")
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => lseOn.Value, name: "LSERDY")
                .WithFlag(2, name: "LSEBYP")
                .WithValueField(3, 2, name: "LSEDRV")
                .WithFlag(5, name: "LSECSSON")
                .WithFlag(6, FieldMode.Read, valueProviderCallback: _ => false, name: "LSECSSD")
                .WithFlag(7, name: "LSESYSDIS")
                .WithValueField(8, 2, name: "RTCSEL")
                .WithReservedBits(10, 5)
                .WithFlag(15, name: "RTCEN",
                    writeCallback: (_, value) => { if(rtc != null) { sysbus.SetPeripheralEnabled(rtc, value); } })
                .WithFlag(16, name: "BDRST")
                .WithReservedBits(17, 7)
                .WithFlag(24, name: "LSCOEN")
                .WithFlag(25, name: "LSCOSEL")
                .WithReservedBits(26, 6);

            // 6.4.30, reset 0x0C00_0600: MSISRANGE=6 and PINRSTF|BORRSTF as after power-on
            Registers.ControlStatus.Define(this, 0x0C000600)
                .WithFlag(0, out var lsiOn, name: "LSION")
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => lsiOn.Value, name: "LSIRDY")
                .WithReservedBits(2, 2)
                .WithFlag(4, name: "LSIPREDIV")
                .WithReservedBits(5, 3)
                .WithValueField(8, 4, name: "MSISRANGE")
                .WithReservedBits(12, 11)
                .WithFlag(23, FieldMode.Write, name: "RMVF",
                    writeCallback: (_, value) => { if(value) { resetFlags.Value = 0; } })
                .WithValueField(24, 8, out resetFlags, FieldMode.Read, name: "RSTF");

            // 6.4.31
            Registers.ClockRecoveryRc.Define(this)
                .WithFlag(0, out var hsi48On, name: "HSI48ON")
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => hsi48On.Value, name: "HSI48RDY")
                .WithReservedBits(2, 5)
                .WithValueField(7, 9, FieldMode.Read, name: "HSI48CAL")
                .WithReservedBits(16, 16);
        }

        // MSIRANGE when MSIRGSEL=1, MSISRANGE (reset 4 MHz) otherwise. RM0432 6.4.1/6.4.30
        private ulong MsiFrequency => msiRangeSelect.Value && msiRange.Value < (ulong)MsiRanges.Length
            ? MsiRanges[msiRange.Value]
            : MsiRanges[6];

        private ulong PllInputFrequency
        {
            get
            {
                switch(pllSource.Value)
                {
                case PllSource.Msi:
                    return MsiFrequency;
                case PllSource.Hsi16:
                    return Hsi16Frequency;
                case PllSource.Hse:
                    return hseFrequency;
                default:
                    return 0;
                }
            }
        }

        // PLLR: 00=2, 01=4, 10=6, 11=8. RM0432 6.4.4
        private ulong PllRFrequency => PllInputFrequency * pllN.Value / (pllM.Value + 1) / (2 * (pllR.Value + 1));

        // HPRE: 0xxx=1, 1000=2 ... 1011=16, 1100=64 ... 1111=512. RM0432 6.4.3
        private static ulong AhbDivider(ulong hpre)
        {
            if((hpre & 0x8) == 0)
            {
                return 1;
            }
            var shift = (int)(hpre & 0x7) + 1;
            return 1UL << (shift >= 5 ? shift + 1 : shift);
        }

        private static readonly ulong[] MsiRanges =
        {
            100000, 200000, 400000, 800000, 1000000, 2000000,
            4000000, 8000000, 16000000, 24000000, 32000000, 48000000,
        };

        private IFlagRegisterField msiRangeSelect;
        private IValueRegisterField msiRange;
        private IEnumRegisterField<SystemClockSource> systemClockSwitch;
        private IValueRegisterField ahbPrescaler;
        private IEnumRegisterField<PllSource> pllSource;
        private IValueRegisterField pllM;
        private IValueRegisterField pllN;
        private IValueRegisterField pllR;
        private IValueRegisterField resetFlags;

        private readonly IHasDivisibleFrequency systick;
        private readonly IPeripheral rtc;
        private readonly ulong hseFrequency;

        private const ulong Hsi16Frequency = 16000000;
        private const ulong DefaultHseFrequency = 16000000;

        private enum SystemClockSource
        {
            Msi,
            Hsi16,
            Hse,
            Pll,
        }

        private enum PllSource
        {
            None,
            Msi,
            Hsi16,
            Hse,
        }

        // Offsets: stm32l4r5xx.h RCC_TypeDef
        private enum Registers
        {
            ClockControl = 0x00,
            InternalClockSourcesCalibration = 0x04,
            ClockConfiguration = 0x08,
            PllConfiguration = 0x0C,
            PllSai1Configuration = 0x10,
            PllSai2Configuration = 0x14,
            ClockInterruptEnable = 0x18,
            ClockInterruptFlag = 0x1C,
            ClockInterruptClear = 0x20,
            Ahb1Reset = 0x28,
            Ahb2Reset = 0x2C,
            Ahb3Reset = 0x30,
            Apb1Reset1 = 0x38,
            Apb1Reset2 = 0x3C,
            Apb2Reset = 0x40,
            Ahb1Enable = 0x48,
            Ahb2Enable = 0x4C,
            Ahb3Enable = 0x50,
            Apb1Enable1 = 0x58,
            Apb1Enable2 = 0x5C,
            Apb2Enable = 0x60,
            Ahb1SleepEnable = 0x68,
            Ahb2SleepEnable = 0x6C,
            Ahb3SleepEnable = 0x70,
            Apb1SleepEnable1 = 0x78,
            Apb1SleepEnable2 = 0x7C,
            Apb2SleepEnable = 0x80,
            IndependentClockConfiguration = 0x88,
            BackupDomainControl = 0x90,
            ControlStatus = 0x94,
            ClockRecoveryRc = 0x98,
            IndependentClockConfiguration2 = 0x9C,
            OctospiDelayConfiguration = 0xA4,
        }
    }
}
