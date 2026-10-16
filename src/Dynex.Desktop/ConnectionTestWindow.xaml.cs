using System.Windows;
using Dynex.Core;

namespace Dynex.Desktop;

public partial class ConnectionTestWindow : Window
{
    private readonly DynexRuntime runtime;
    private CancellationTokenSource? running;
    public ConnectionTestWindow(DynexRuntime runtime, Guid? selectedDeviceId = null)
    {
        this.runtime = runtime;
        InitializeComponent();
        var entries = runtime.Devices;
        TargetBox.ItemsSource = entries;
        TargetBox.SelectedItem = entries.FirstOrDefault(d => d.DeviceId == selectedDeviceId) ?? entries.FirstOrDefault(d => d.Online) ?? entries.FirstOrDefault();
        StartButton.IsEnabled = entries.Count > 0;
        Feedback.Text = entries.Count > 0 ? "Ein früherer Online-Status ersetzt diesen aktiven Test nicht." : "Noch kein authentifiziertes Gerät bekannt. DYNEX am zweiten PC starten und Geräte erneut öffnen.";
        Closed += (_, _) => running?.Cancel();
    }
    private async void StartClick(object sender, RoutedEventArgs args)
    {
        if (running is not null || TargetBox.SelectedItem is not DeviceRegistryEntry selected) return;
        using var operation = new CancellationTokenSource();
        running = operation; StartButton.IsEnabled = false; TargetBox.IsEnabled = false; CancelButton.IsEnabled = true;
        Feedback.Text = "Gezielte Probe läuft …"; Results.ItemsSource = null;
        try
        {
            var result = await runtime.TestPeerConnectionAsync(selected.DeviceId, operation.Token);
            Results.ItemsSource = result.Stages.Select(s => new StageRow(s.Name, Label(s.State), s.Detail)).ToArray();
            Feedback.Text = result.Succeeded ? "Beide Richtungen, Authentifizierung und Version bestätigt." : "Ergebnis je Stufe beachten. Unbekannt bestätigt weder Erfolg noch eine Firewall-Ursache.";
        }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or System.Net.Sockets.SocketException or ObjectDisposedException)
        { Feedback.Text = "Test konnte nicht abgeschlossen werden: " + error.Message; }
        finally { running = null; StartButton.IsEnabled = true; TargetBox.IsEnabled = true; CancelButton.IsEnabled = false; }
    }
    private static string Label(ConnectionTestState state) => state switch { ConnectionTestState.Passed => "Bestätigt", ConnectionTestState.Failed => "Abweichung", ConnectionTestState.Cancelled => "Abgebrochen", _ => "Unbekannt" };
    private sealed record StageRow(string Name, string State, string Detail);
    private void CancelClick(object sender, RoutedEventArgs args) => running?.Cancel();
    private void CloseClick(object sender, RoutedEventArgs args) => Close();
}
