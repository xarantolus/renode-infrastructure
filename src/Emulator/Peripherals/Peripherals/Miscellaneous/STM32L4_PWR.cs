//
// Copyright (c) 2026 Philipp Erhardt <philipp.erhardt@tum.de>
//
//  This file is licensed under the MIT License.
//  Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // STM32L4+ power control. Register layout: RM0432 Rev 9 section 5.4 and ST's stm32l4r5xx.h
    // (PWR_TypeDef). Voltage-scaling and regulator-mode changes complete immediately, so VOSF
    // never reads busy and REGLPF follows LPR. Low-power modes and wakeup sources are not modelled.
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class STM32L4_PWR : BasicDoubleWordPeripheral, IKnownSize
    {
        public STM32L4_PWR(IMachine machine) : base(machine)
        {
            DefineRegisters();
        }

        public long Size => 0x400;

        private void DefineRegisters()
        {
            // 5.4.1, reset 0x0000_0200 (VOS = Range 1)
            Registers.Control1.Define(this, 0x00000200)
                .WithValueField(0, 3, name: "LPMS")
                .WithReservedBits(3, 1)
                .WithFlag(4, name: "RRSTP")
                .WithReservedBits(5, 3)
                .WithFlag(8, name: "DBP")
                .WithValueField(9, 2, name: "VOS")
                .WithReservedBits(11, 3)
                .WithFlag(14, out lowPowerRun, name: "LPR")
                .WithReservedBits(15, 17);

            Registers.Control2.Define(this).WithValueField(0, 32, name: "CR2");
            // 5.4.3, reset 0x0000_8000 (EIWUL)
            Registers.Control3.Define(this, 0x00008000).WithValueField(0, 32, name: "CR3");
            Registers.Control4.Define(this).WithValueField(0, 32, name: "CR4");

            // 5.4.5: no wakeup or standby events are generated
            Registers.Status1.Define(this).WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ => 0, name: "SR1");

            // 5.4.6
            Registers.Status2.Define(this)
                .WithReservedBits(0, 8)
                .WithFlag(8, FieldMode.Read, valueProviderCallback: _ => true, name: "REGLPS")
                .WithFlag(9, FieldMode.Read, valueProviderCallback: _ => lowPowerRun.Value, name: "REGLPF")
                .WithFlag(10, FieldMode.Read, valueProviderCallback: _ => false, name: "VOSF")
                .WithFlag(11, FieldMode.Read, valueProviderCallback: _ => false, name: "PVDO")
                .WithValueField(12, 4, FieldMode.Read, valueProviderCallback: _ => 0, name: "PVMO")
                .WithReservedBits(16, 16);

            Registers.StatusClear.Define(this).WithValueField(0, 32, FieldMode.Write, name: "SCR");

            // 5.4.8-5.4.25: GPIO pull-up/down configuration in Standby/Shutdown
            for(var offset = (long)Registers.PullUpControlA; offset <= (long)Registers.PullDownControlI; offset += 4)
            {
                ((Registers)offset).Define(this).WithValueField(0, 32, name: "PUCR/PDCR");
            }

            // 5.4.26, reset 0x0000_0100 (R1MODE = 1, boost disabled)
            Registers.Control5.Define(this, 0x00000100)
                .WithReservedBits(0, 8)
                .WithFlag(8, name: "R1MODE")
                .WithReservedBits(9, 23);
        }

        private IFlagRegisterField lowPowerRun;

        // Offsets: stm32l4r5xx.h PWR_TypeDef
        private enum Registers
        {
            Control1 = 0x00,
            Control2 = 0x04,
            Control3 = 0x08,
            Control4 = 0x0C,
            Status1 = 0x10,
            Status2 = 0x14,
            StatusClear = 0x18,
            PullUpControlA = 0x20,
            PullDownControlI = 0x64,
            Control5 = 0x80,
        }
    }
}
