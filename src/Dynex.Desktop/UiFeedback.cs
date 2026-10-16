using System.Media;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfKeyEventHandler = System.Windows.Input.KeyEventHandler;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Dynex.Desktop;

/// <summary>Small, optional interaction feedback for user initiated UI actions.</summary>
public sealed class UiFeedback : IDisposable
{
    private readonly Window _owner;
    private readonly Dictionary<FrameworkElement, TransitionState> _transitions = [];
    private MemoryStream? _clickWave;
    private SoundPlayer? _clickPlayer;
    private readonly bool _silent;
    private WpfButtonBase? _mouseClickCandidate;
    private WpfButtonBase? _keyClickCandidate;
    private bool _soundsEnabled;
    private bool _animationsEnabled = true;
    private bool _disposed;

    public UiFeedback(Window owner, bool silent)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _silent = silent;
        _soundsEnabled = true;
        try
        {
            _clickWave = CreateClickWave();
            _clickPlayer = new SoundPlayer(_clickWave);
            _clickPlayer.Load();
        }
        catch
        {
            _clickPlayer?.Dispose();
            _clickWave?.Dispose();
            _clickPlayer = null;
            _clickWave = null;
        }

        _owner.Closed += OnOwnerClosed;
        _owner.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown), true);
        _owner.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnPreviewMouseUp), true);
        _owner.AddHandler(Keyboard.PreviewKeyDownEvent, new WpfKeyEventHandler(OnPreviewKeyDown), true);
        _owner.AddHandler(Keyboard.PreviewKeyUpEvent, new WpfKeyEventHandler(OnPreviewKeyUp), true);
        _owner.AddHandler(WpfButtonBase.ClickEvent, new RoutedEventHandler(OnButtonClick), true);
    }

    /// <summary>Whether synthesized interaction clicks may play. No sounds play unless PlayClick is called.</summary>
    public bool SoundsEnabled
    {
        get => _soundsEnabled;
        set => _soundsEnabled = value;
    }

    /// <summary>Whether short page transitions may animate.</summary>
    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set
        {
            _animationsEnabled = value;
            if (!value)
                RunOnOwnerDispatcher(FinishAllTransitions);
        }
    }

    /// <summary>Plays a short synthesized click asynchronously when sounds are enabled.</summary>
    public void PlayClick()
    {
        if (_disposed || _silent || !_soundsEnabled || _clickPlayer is null)
            return;

        try
        {
            _clickPlayer.Play();
        }
        catch
        {
            // Optional feedback must never interrupt the action that requested it.
        }
    }

    /// <summary>Applies a brief fade and small upward settle to a newly shown page or panel.</summary>
    public void Transition(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (_disposed)
            return;

        if (!element.Dispatcher.CheckAccess())
        {
            element.Dispatcher.BeginInvoke(new Action(() => Transition(element)), DispatcherPriority.Normal);
            return;
        }

        if (!_animationsEnabled || !SystemParameters.ClientAreaAnimation)
        {
            FinishTransition(element);
            return;
        }

        FinishTransition(element);

        var baseTransform = element.RenderTransform;
        var translate = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(baseTransform?.CloneCurrentValue() ?? Transform.Identity);
        group.Children.Add(translate);
        element.RenderTransform = group;

        var state = new TransitionState(baseTransform, translate);
        _transitions[element] = state;

        var settle = new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        settle.Completed += (_, _) => FinishTransition(element, state);
        translate.BeginAnimation(TranslateTransform.YProperty, settle, HandoffBehavior.SnapshotAndReplace);

        var fade = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _owner.Closed -= OnOwnerClosed;
        _owner.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown));
        _owner.RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnPreviewMouseUp));
        _owner.RemoveHandler(Keyboard.PreviewKeyDownEvent, new WpfKeyEventHandler(OnPreviewKeyDown));
        _owner.RemoveHandler(Keyboard.PreviewKeyUpEvent, new WpfKeyEventHandler(OnPreviewKeyUp));
        _owner.RemoveHandler(WpfButtonBase.ClickEvent, new RoutedEventHandler(OnButtonClick));

        if (_owner.Dispatcher.CheckAccess())
            FinishAllTransitions();
        else if (!_owner.Dispatcher.HasShutdownStarted)
            _owner.Dispatcher.BeginInvoke(new Action(FinishAllTransitions), DispatcherPriority.Normal);

        try { _clickPlayer?.Stop(); } catch { }
        _clickPlayer?.Dispose();
        _clickWave?.Dispose();
    }

    private void OnOwnerClosed(object? sender, EventArgs e) => Dispose();

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _mouseClickCandidate = e.ChangedButton == MouseButton.Left ? FindButton(e.OriginalSource as DependencyObject) : null;
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        var candidate = _mouseClickCandidate;
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (ReferenceEquals(candidate, _mouseClickCandidate))
                _mouseClickCandidate = null;
        }));
    }

    private void OnPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
            _keyClickCandidate = FindButton(Keyboard.FocusedElement as DependencyObject);
    }

    private void OnPreviewKeyUp(object sender, WpfKeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter))
            return;
        var candidate = _keyClickCandidate;
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (ReferenceEquals(candidate, _keyClickCandidate))
                _keyClickCandidate = null;
        }));
    }

    private void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (FindButton(e.OriginalSource as DependencyObject) is not { } button)
            return;

        var isMouseInput = ReferenceEquals(_mouseClickCandidate, button);
        var isKeyboardInput = ReferenceEquals(_keyClickCandidate, button);
        if (!isMouseInput && !isKeyboardInput)
            return;

        if (isMouseInput)
            _mouseClickCandidate = null;
        if (isKeyboardInput)
            _keyClickCandidate = null;
        PlayClick();
    }

    private static WpfButtonBase? FindButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is WpfButtonBase button)
                return button;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private void RunOnOwnerDispatcher(Action action)
    {
        if (_owner.Dispatcher.CheckAccess())
            action();
        else if (!_owner.Dispatcher.HasShutdownStarted)
            _owner.Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    private void FinishAllTransitions()
    {
        foreach (var pair in _transitions.ToArray())
            FinishTransition(pair.Key, pair.Value);
    }

    private void FinishTransition(FrameworkElement element)
    {
        if (_transitions.TryGetValue(element, out var state))
            FinishTransition(element, state);
        else
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        }
    }

    private void FinishTransition(FrameworkElement element, TransitionState state)
    {
        if (!_transitions.TryGetValue(element, out var current) || !ReferenceEquals(current, state))
            return;

        _transitions.Remove(element);
        state.Translate.BeginAnimation(TranslateTransform.YProperty, null);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.RenderTransform = state.OriginalTransform;
        element.Opacity = 1;
    }

    private static MemoryStream CreateClickWave()
    {
        const int sampleRate = 22050;
        const int sampleCount = 900;
        const short channels = 1;
        const short bitsPerSample = 16;
        var stream = new MemoryStream(44 + sampleCount * 2);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + sampleCount * 2);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bitsPerSample / 8);
            writer.Write((short)(channels * bitsPerSample / 8));
            writer.Write(bitsPerSample);
            writer.Write("data"u8);
            writer.Write(sampleCount * 2);

            for (var i = 0; i < sampleCount; i++)
            {
                var progress = (double)i / sampleCount;
                var frequency = 1450 - 500 * progress;
                var envelope = Math.Min(1, i / 90d) * Math.Exp(-progress * 8.5);
                var sample = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * envelope * 2200);
                writer.Write(sample);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private sealed record TransitionState(Transform? OriginalTransform, TranslateTransform Translate);
}
