using OpenTap;

namespace InstrumentComponents.OpenTap;

[Display("Switch Close Route", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Switch], Description: "Close a matrix route.")]
public sealed class SwitchCloseRouteStep : InstrumentBoundStep<SwitchInstrument>
{
    [Display("Channel 1", Order: 2, Description: "First 1-based matrix channel.")]
    public uint Channel1 { get; set; } = 1;

    [Display("Channel 2", Order: 3, Description: "Second 1-based matrix channel.")]
    public uint Channel2 { get; set; } = 2;

    public SwitchCloseRouteStep()
    {
        AddRouteRules();
    }

    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        instrument.Matrix.CloseRoute(Channel1, Channel2);
        UpgradeVerdict(Verdict.Pass);
    }

    private void AddRouteRules()
    {
        Rules.Add(() => Channel1 >= 1, "Channel 1 must be at least 1.", nameof(Channel1));
        Rules.Add(() => Channel2 >= 1, "Channel 2 must be at least 1.", nameof(Channel2));
        Rules.Add(() => Channel1 != Channel2, "Route endpoints must differ.", nameof(Channel1), nameof(Channel2));
    }
}

[Display("Switch Open Route", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Switch], Description: "Open a matrix route.")]
public sealed class SwitchOpenRouteStep : InstrumentBoundStep<SwitchInstrument>
{
    [Display("Channel 1", Order: 2, Description: "First 1-based matrix channel.")]
    public uint Channel1 { get; set; } = 1;

    [Display("Channel 2", Order: 3, Description: "Second 1-based matrix channel.")]
    public uint Channel2 { get; set; } = 2;

    public SwitchOpenRouteStep()
    {
        Rules.Add(() => Channel1 >= 1, "Channel 1 must be at least 1.", nameof(Channel1));
        Rules.Add(() => Channel2 >= 1, "Channel 2 must be at least 1.", nameof(Channel2));
        Rules.Add(() => Channel1 != Channel2, "Route endpoints must differ.", nameof(Channel1), nameof(Channel2));
    }

    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        instrument.Matrix.OpenRoute(Channel1, Channel2);
        UpgradeVerdict(Verdict.Pass);
    }
}

[Display("Switch Open All", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Switch], Description: "Open all routes.")]
public sealed class SwitchOpenAllStep : InstrumentBoundStep<SwitchInstrument>
{
    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        instrument.Matrix.OpenAll();
        UpgradeVerdict(Verdict.Pass);
    }
}
