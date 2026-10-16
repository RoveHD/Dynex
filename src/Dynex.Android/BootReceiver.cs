using global::Android.App;
using global::Android.Content;

namespace Dynex.Android;

[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter(new[] { Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced })]
public sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action is not (Intent.ActionBootCompleted or Intent.ActionMyPackageReplaced)) return;
        if (!MobileSettingsStore.IsEnabled(context) || !MobileSettingsStore.Load(context).AutoStart) return;
        try { context.StartForegroundService(new Intent(context, typeof(DynexVpnService)).SetAction("Resume")); }
        catch (Java.Lang.Exception ex)
        {
            global::Android.Util.Log.Warn("DYNEX", "Android verhindert Autostart: " + ex.GetType().Name);
        }
    }
}
