using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Dynex.Desktop;

public partial class DeviceManagerWindow : Window
{
    private readonly DynexRuntime runtime;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool refreshing;
    public DeviceManagerWindow(DynexRuntime runtime)
    {
        this.runtime = runtime;
        InitializeComponent();
        Refresh();
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => timer.Start();
        Closed += (_, _) => timer.Stop();
    }
    private void Refresh()
    {
        var selected = (DeviceGrid.SelectedItem as DeviceRegistryEntry)?.DeviceId;
        refreshing = true;
        DeviceGrid.ItemsSource = runtime.Devices;
        DeviceGrid.SelectedItem = runtime.Devices.FirstOrDefault(d => d.DeviceId == selected);
        refreshing = false;
        if (!string.IsNullOrWhiteSpace(runtime.DeviceRegistryError)) Feedback.Text = runtime.DeviceRegistryError;
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (refreshing) return;
        var selected = DeviceGrid.SelectedItem as DeviceRegistryEntry;
        SaveButton.IsEnabled = selected is not null;
        AliasBox.Text = selected?.Device.Alias ?? "";
        DispositionBox.SelectedIndex = selected is null ? 0 : (int)selected.Device.Disposition;
    }
    private async void SaveClick(object sender, RoutedEventArgs args)
    {
        if (DeviceGrid.SelectedItem is not DeviceRegistryEntry selected) return;
        SaveButton.IsEnabled = false;
        try
        {
            await runtime.SetDevicePreferencesAsync(selected.DeviceId, AliasBox.Text, (DeviceDisposition)Math.Max(0, DispositionBox.SelectedIndex));
            Feedback.Text = "Gespeichert. Ausschluss wirkt sofort und bleibt nach Neustart erhalten.";
            Refresh();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        { Feedback.Text = error.Message; }
        finally { SaveButton.IsEnabled = DeviceGrid.SelectedItem is not null; }
    }
    private void TestClick(object sender, RoutedEventArgs args)
    {
        var selected = (DeviceGrid.SelectedItem as DeviceRegistryEntry)?.DeviceId;
        new ConnectionTestWindow(runtime, selected) { Owner = this }.ShowDialog();
    }
    private void CloseClick(object sender, RoutedEventArgs args) => Close();
}
