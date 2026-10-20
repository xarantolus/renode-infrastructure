//
// Copyright (c) 2026 Philipp Erhardt <philipp.erhardt@tum.de>
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.DMA;

namespace Antmicro.Renode.Peripherals.Analog
{
    // STM32L4+ ADC1: 19 channels (IN0 = VREFINT, IN17 = temperature sensor, IN18 = VBAT/3), single ADC,
    // injected sequence, offsets, differential inputs (RM0432 Rev 9 section 21.4.11; stm32l4r5xx.h ADC_TypeDef).
    public class STM32L4_ADC : STM32_ADC_Common
    {
        public STM32L4_ADC(IMachine machine, double referenceVoltage, uint externalEventFrequency, int dmaChannel = 0, IDMA dmaPeripheral = null)
            : base(
                machine,
                referenceVoltage,
                externalEventFrequency,
                dmaChannel,
                dmaPeripheral,
                // Base class configuration
                watchdogCount: 3,
                hasCalibration: false,
                channelCount: 19,
                hasPrescaler: true,
                hasVbatPin: true,
                hasChannelSelect: false,
                hasChannelSequence: true,
                hasPowerRegister: false,
                hasOffset: true,
                hasDifferentialMode: true,
                samplingTime: SamplingTime.PerChannel,
                dualMode: false,
                hasLinearityCalibration: false,
                hasChannelInjection: true,
                hasSeparateThresholdRegisters: false,
                resolutionRange: ResolutionRange.Bits6_12,
                hasChannelPreselection: false,
                hasScanDirection: false,
                hasL4RegisterLayout: true
            )
        { }
    }
}
