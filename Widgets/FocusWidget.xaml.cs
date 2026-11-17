using System.Media;
using System.Windows;
using System.Windows.Media.Animation;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

/// <summary>
/// Pomodoro-style focus timer: focus → break → next focus session, cycling through the configured session count.
/// A finished focus phase rolls straight into its break; a finished break waits for the user to start the next session.
/// </summary>
public partial class FocusWidget : DockWidget
{
    private enum Phase { Focus, Break }

    private Phase _phase = Phase.Focus;
    private int _session = 1;
    private TimeSpan _remaining;
    private TimeSpan _lastFullLength;
    private DateTime? _endsAt;
    private bool _initialized;

    public FocusWidget()
    {
        InitializeComponent();
    }

    protected override void OnActivated() => Render();
    protected override void OnLanguageChanged() => Render();

    private TimeSpan PhaseLength => TimeSpan.FromMinutes(_phase == Phase.Focus ? Settings.FocusMinutes : Settings.FocusBreakMinutes);
    private bool IsRunning => _endsAt is not null;

    protected override void OnSettingsChanged()
    {
        _session = Math.Min(_session, Settings.FocusSessions);
        // Idle timers follow edited lengths immediately; a running phase keeps its deadline.
        if (!_initialized || (!IsRunning && _remaining == _lastFullLength))
        {
            _remaining = PhaseLength;
            _initialized = true;
        }

        _lastFullLength = PhaseLength;
        Render();
    }

    protected override void OnTick(DateTime now)
    {
        if (_endsAt is not { } endsAt) return;
        _remaining = endsAt - now;
        if (_remaining <= TimeSpan.Zero) CompletePhase();
        Render();
    }

    private void CompletePhase()
    {
        SystemSounds.Asterisk.Play();
        Pulse();
        if (_phase == Phase.Focus)
        {
            StartPhase(Phase.Break, run: true);
        }
        else
        {
            _session = _session >= Settings.FocusSessions ? 1 : _session + 1;
            StartPhase(Phase.Focus, run: false);
        }
    }

    private void StartPhase(Phase phase, bool run)
    {
        _phase = phase;
        _remaining = PhaseLength;
        _lastFullLength = _remaining;
        _endsAt = run ? DateTime.Now + _remaining : null;
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunning)
        {
            _remaining = _endsAt!.Value - DateTime.Now;
            _endsAt = null;
        }
        else
        {
            if (_remaining <= TimeSpan.Zero) _remaining = PhaseLength;
            _endsAt = DateTime.Now + _remaining;
        }

        Render();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        var wasRunning = IsRunning;
        if (_phase == Phase.Focus)
        {
            StartPhase(Phase.Break, wasRunning);
        }
        else
        {
            _session = _session >= Settings.FocusSessions ? 1 : _session + 1;
            StartPhase(Phase.Focus, wasRunning);
        }

        Render();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _session = 1;
        StartPhase(Phase.Focus, run: false);
        Render();
    }

    private void Render()
    {
        var remaining = _remaining < TimeSpan.Zero ? TimeSpan.Zero : _remaining;
        // Round up so the display reads 25:00 at the start and 00:00 only at the end.
        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        RemainingText.Text = $"{seconds / 60:00}:{seconds % 60:00}";
        PhaseText.Text = UiText.Get(_phase == Phase.Focus ? "Focus" : "Break");
        SessionText.Text = $" {_session}/{Settings.FocusSessions}";
        ToggleGlyph.Content = FindResource(IsRunning ? "IconPauseFill" : "IconPlayFill");
        PlayButton.ToolTip = UiText.Get(IsRunning ? "FocusPause" : "FocusStart");
        System.Windows.Automation.AutomationProperties.SetName(PlayButton, (string)PlayButton.ToolTip);
        Card.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty,
            IsRunning ? (_phase == Phase.Focus ? "WidgetFocusAccent" : "WidgetAccent") : "WidgetTrack");
        PhaseText.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,
            _phase == Phase.Focus ? "WidgetFocusAccent" : "WidgetAccent");
    }

    private void Pulse()
    {
        if (!Settings.AnimationsEnabled || !SystemParameters.ClientAreaAnimation) return;
        var pulse = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(260))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3)
        };
        Card.BeginAnimation(OpacityProperty, pulse);
    }
}
