using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Core.Contracts;
using Emergency_Response_Simulator.Core.Model;
using Emergency_Response_Simulator.Simulation.State;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>One message in the comms feed.</summary>
public sealed record CommsRow(
    Guid Id,
    string Time,
    string Channel,
    string Speakers,
    string Text,
    CommsQuality Quality,
    string Notes,
    bool FromControl,
    bool CanRepeat);

/// <summary>A radio channel as heard from the control room: who is talking and how busy it is.</summary>
public sealed record ChannelRow(string Id, string Name, string OnAir, double Utilisation, string Load, string Patched, bool Busy, bool Congested);

public sealed record MissedCallRow(Guid Id, string Time, string Detail);

public sealed record PatchRow(Guid Id, string Channels, string State);

/// <summary>
/// The communications hub in the right panel (Design Document §13): channel toggles, live channel status, the
/// comms feed (radio, 999 line, agency chat) with "say again", a push-to-talk composer, missed 999 calls, patches
/// and channel assignment. Shows only what command heard, through <see cref="CopView"/>.
/// </summary>
public partial class CommsHubViewModel : ObservableObject
{
    private const int MaxFeed = 150;

    private readonly MainViewModel _owner;
    private readonly IC2Service _c2;
    private readonly CopView _cop;
    private readonly Stopwatch _ptt = new();

    public CommsHubViewModel(MainViewModel owner, IC2Service c2, CopView cop)
    {
        _owner = owner;
        _c2 = c2;
        _cop = cop;
        foreach (var filter in Filters)
            filter.PropertyChanged += (_, _) => RefreshFeed();

        // Who is on air changes with time, not only with events.
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => RefreshChannels(),
            Application.Current.Dispatcher);
        timer.Start();
    }

    /// <summary>Which traffic the feed shows.</summary>
    public ObservableCollection<ToggleItem> Filters { get; } =
    [
        new("fire", "Fire", true),
        new("ems", "Ambulance", true),
        new("police", "Garda", true),
        new("interagency", "Inter-agency", true),
        new("other", "Other services", true),
        new("calls", "999 calls", true),
        new("chat", "Agency chat", true),
    ];

    public ObservableCollection<CommsRow> Feed { get; } = [];
    public ObservableCollection<ChannelRow> ChannelStates { get; } = [];
    public ObservableCollection<MissedCallRow> MissedCalls { get; } = [];
    public ObservableCollection<PatchRow> Patches { get; } = [];

    /// <summary>Every radio channel command knows of (for patching).</summary>
    public ObservableCollection<string> AllRadioChannels { get; } = [];

    /// <summary>Channels command can talk on.</summary>
    public ObservableCollection<string> TalkChannels { get; } = [];

    /// <summary>Call signs of crews on the selected talk channel.</summary>
    public ObservableCollection<string> CallSigns { get; } = [];

    public ObservableCollection<OrderTarget> Units { get; } = [];

    // ---- Composer ----

    [ObservableProperty] private string? _talkChannel = RadioPlan.FireCommand;
    [ObservableProperty] private string? _talkTo;
    [ObservableProperty] private string _talkText = "";
    [ObservableProperty] private bool _talking;
    [ObservableProperty] private string _talkChannelState = "";
    [ObservableProperty] private bool _talkChannelBusy;

    /// <summary>"Engine 4, Control, " to start a well-formed call.</summary>
    partial void OnTalkToChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || TalkText.StartsWith(value, StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(TalkText))
            TalkText = $"{value}, Control, ";
    }

    partial void OnTalkChannelChanged(string? value)
    {
        RefreshCallSigns();
        RefreshChannels();
    }

    /// <summary>Push-to-talk pressed: the button is held while speaking.</summary>
    public void PressTalk()
    {
        if (string.IsNullOrWhiteSpace(TalkText) || TalkChannel is null) return;
        Talking = true;
        _ptt.Restart();
    }

    /// <summary>Push-to-talk released: what was said goes out, cut off if the button was let go too early.</summary>
    public async Task ReleaseTalkAsync()
    {
        if (!Talking) return;
        Talking = false;
        _ptt.Stop();
        var held = _ptt.Elapsed.TotalSeconds;
        var channel = TalkChannel!;
        var result = await _owner.RunCommandAsync(() => _c2.TransmitAsync(channel, TalkTo, TalkText, held),
            $"Transmitted on {channel} ({held:F1} s)");
        if (result?.Succeeded == true) TalkText = "";
    }

    /// <summary>Send without push-to-talk timing (keyboard users).</summary>
    [RelayCommand]
    private async Task SendAsync()
    {
        if (TalkChannel is not { } channel) return;
        var result = await _owner.RunCommandAsync(() => _c2.TransmitAsync(channel, TalkTo, TalkText), $"Transmitted on {channel}");
        if (result?.Succeeded == true) TalkText = "";
    }

    // ---- Actions ----

    [RelayCommand]
    private Task RequestRepeatAsync(Guid messageId) => _owner.RunCommandAsync(() => _c2.RequestRepeatAsync(messageId), "Asked to say again");

    [RelayCommand]
    private Task CallBackAsync(Guid callId) => _owner.RunCommandAsync(() => _c2.CallBackAsync(callId), "Calling back");

    [ObservableProperty] private string? _patchA;
    [ObservableProperty] private string? _patchB = RadioPlan.FireCommand;

    [RelayCommand]
    private Task PatchAsync() => PatchA is { } a && PatchB is { } b
        ? _owner.RunCommandAsync(() => _c2.PatchChannelsAsync(a, b), $"Patch requested: {a} ↔ {b} (a few minutes to set up)")
        : Task.CompletedTask;

    [RelayCommand]
    private Task RemovePatchAsync(Guid patchId) => _owner.RunCommandAsync(() => _c2.RemovePatchAsync(patchId), "Patch removed");

    [ObservableProperty] private OrderTarget? _moveUnit;
    [ObservableProperty] private string? _moveChannel = RadioPlan.FireTac2;

    [RelayCommand]
    private Task MoveUnitAsync() => MoveUnit is { Id: { } unit } target && MoveChannel is { } channel
        ? _owner.RunCommandAsync(() => _c2.AssignChannelAsync(unit, channel), $"{target.Label} moved to {channel}")
        : Task.CompletedTask;

    // ---- Refresh ----

    public void Refresh()
    {
        Replace(AllRadioChannels, _cop.Channels.Where(c => c.Info.Kind == ChannelKind.Radio).Select(c => c.Info.Id).Order());
        var heard = _cop.HeardChannels();
        var talk = _cop.Channels.Where(c => c.Info.Kind == ChannelKind.Radio && heard.Contains(c.Info.Id)).Select(c => c.Info.Id).ToList();
        if (!talk.SequenceEqual(TalkChannels))
        {
            var selected = TalkChannel;
            Replace(TalkChannels, talk);
            TalkChannel = selected is not null && talk.Contains(selected) ? selected : talk.FirstOrDefault();
        }

        var units = _cop.Units.Where(u => u.Agency?.AiControlled != true).OrderBy(u => u.Callsign, StringComparer.Ordinal)
            .Select(u => new OrderTarget(OrderTargetKind.Unit, u.Id, null, $"{u.Callsign} ({u.Channel})")).ToList();
        if (!units.Select(u => u.Label).SequenceEqual(Units.Select(u => u.Label)))
        {
            var selected = MoveUnit?.Id;
            Replace(Units, units);
            MoveUnit = Units.FirstOrDefault(u => u.Id == selected);
        }

        Replace(MissedCalls, _cop.MissedCalls.Where(m => m.CalledBackAt is null).OrderByDescending(m => m.At)
            .Select(m => new MissedCallRow(m.Id, MainViewModel.Time(m.At),
                $"Hung up after {m.Waited.TotalSeconds:F0} s" + (m.AccuracyMeters is { } a ? $" · phone within ~{a:F0} m" : ""))));
        Replace(Patches, _cop.Patches.Select(p => new PatchRow(p.Id, $"{p.ChannelA} ↔ {p.ChannelB}",
            p.ActiveFrom is { } from ? $"working since {MainViewModel.Time(from)}" : "being set up")));

        RefreshCallSigns();
        RefreshFeed();
        RefreshChannels();
    }

    private void RefreshCallSigns()
    {
        if (TalkChannel is not { } channel) return;
        var linked = _cop.Linked(channel);
        Replace(CallSigns, _cop.Units.Where(u => u.Channel is { } c && linked.Contains(c)).Select(u => u.Callsign).Order(StringComparer.Ordinal));
    }

    private void RefreshFeed()
    {
        bool On(string key) => Filters.First(f => f.Key == key).IsOn;
        bool Shown(CommsEntry entry) => entry.ChannelId switch
        {
            RadioPlan.Calls => On("calls"),
            RadioPlan.Chat => On("chat"),
            RadioPlan.InterAgency => On("interagency"),
            _ => RadioPlan.Find(entry.ChannelId)?.Agency switch
            {
                AgencyType.Fire => On("fire"),
                AgencyType.Ems => On("ems"),
                AgencyType.Police => On("police"),
                _ => On("other"),
            },
        };

        var rows = _cop.CommsLog.Where(Shown).Reverse().Take(MaxFeed).Select(m => new CommsRow(
            m.Id, MainViewModel.Time(m.At, seconds: true), m.ChannelId,
            m.To is { } to ? $"{m.From} → {to}" : m.From,
            m.Text, m.Quality, string.Join(" ", m.Notes), m.FromControl,
            !m.FromControl && !m.RepeatRequested && m.Quality != CommsQuality.Clear && m.ChannelId is not (RadioPlan.Calls or RadioPlan.Chat)));
        Replace(Feed, rows);
    }

    private void RefreshChannels()
    {
        var now = _owner.Now;
        var heard = _cop.HeardChannels();
        var patches = _cop.Patches.Where(p => p.ActiveFrom is not null).ToList();
        var rows = _cop.Channels.Where(c => c.Info.Kind == ChannelKind.Radio && heard.Contains(c.Info.Id)).Select(c =>
        {
            var busy = c.IsBusy(now);
            var load = c.Utilisation(now);
            var patched = string.Join(", ", patches.Where(p => p.ChannelA == c.Info.Id || p.ChannelB == c.Info.Id)
                .Select(p => p.ChannelA == c.Info.Id ? p.ChannelB : p.ChannelA));
            return new ChannelRow(c.Info.Id, c.Info.Name, busy ? $"TX {c.Talker}" : "clear", load, $"{load:P0}",
                patched.Length > 0 ? $"patched: {patched}" : "", busy, load >= 0.75);
        }).ToList();
        Replace(ChannelStates, rows);

        var selected = rows.FirstOrDefault(r => r.Id == TalkChannel);
        TalkChannelBusy = selected?.Busy == true;
        TalkChannelState = selected is null ? "" : selected.Busy ? $"{selected.OnAir}: wait for a gap" : "Channel clear";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var list = items.ToList();
        if (list.SequenceEqual(target)) return;
        target.Clear();
        foreach (var item in list)
            target.Add(item);
    }
}
