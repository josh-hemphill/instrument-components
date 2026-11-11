using OpenTap;

namespace InstrumentComponents.OpenTap;

[Display("Identity Query", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Identity], Description: "Query identity through the selected driver.")]
public sealed class IdentityQueryStep : InstrumentBoundStep<DeviceInstrument>
{
    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        var idn = instrument.QueryIdentity();
        Log.Info("IDN={0}", $"{idn.Manufacturer},{idn.Model},{idn.Serial},{idn.Revision}");
        PhaseIResults.PublishIdentity(Results, $"{idn.Manufacturer},{idn.Model},{idn.Serial},{idn.Revision}", string.Empty);
        UpgradeVerdict(Verdict.Pass);
    }
}

[Display("Safe Shutdown", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Safety], Description: "Driver shutdown, then a separate reset.")]
public sealed class SafeShutdownStep : InstrumentBoundStep<DeviceInstrument>
{
    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        instrument.Shutdown();
        instrument.Reset();
        Log.Info("Safe shutdown complete for {0}", instrument.Name);
        UpgradeVerdict(Verdict.Pass);
    }
}
