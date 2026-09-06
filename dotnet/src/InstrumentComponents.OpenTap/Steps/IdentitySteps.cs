using OpenTap;

namespace InstrumentComponents.OpenTap;

[Display("Identity Query", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Identity], Description: "Query instrument *IDN?.")]
public sealed class IdentityQueryStep : InstrumentBoundStep<ScpiInstrument>
{
    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        var idn = instrument.QueryIdn();
        Log.Info("IDN={0}", idn.FormatResponse());
        PhaseIResults.PublishIdentity(Results, idn.FormatResponse(), string.Empty);
        UpgradeVerdict(Verdict.Pass);
    }
}

[Display("Safe Shutdown", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Safety], Description: "Output off, then *RST.")]
public sealed class SafeShutdownStep : InstrumentBoundStep<ScpiInstrument>
{
    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        instrument.OutputOff();
        instrument.Reset();
        Log.Info("Safe shutdown complete for {0}", instrument.Name);
        UpgradeVerdict(Verdict.Pass);
    }
}
