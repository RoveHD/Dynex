namespace Dynex.Core;

public sealed record RouterGuideText(string Router, IReadOnlyList<string> Steps);

/// <summary>Step-by-step router settings that reduce lag under load, chosen from the UPnP router identity.</summary>
public static class RouterGuide
{
    public static RouterGuideText For(string? manufacturer, string? model, string? friendlyName)
    {
        var id = $"{manufacturer} {model} {friendlyName}".ToLowerInvariant();
        var name = string.Join(" ", new[] { manufacturer, model }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } n ? n : friendlyName ?? "Unbekannter Router";
        if (id.Contains("fritz") || id.Contains("avm"))
            return new(name, [
                "http://fritz.box öffnen und anmelden.",
                "Internet → Filter → Priorisierung: unter „Echtzeitanwendungen“ den Gaming-PC (Gerät) hinzufügen.",
                "Heimnetz → Netzwerk → Netzwerkeinstellungen: „Statusinformationen über UPnP übertragen“ aktivieren, damit DYNEX den Gesamtverkehr sieht.",
                "WLAN → Funkkanal: „Funkkanal-Einstellungen automatisch setzen“ aktiv lassen; Gaming-PC möglichst per LAN-Kabel verbinden."]);
        if (id.Contains("speedport") || id.Contains("telekom"))
            return new(name, [
                "http://speedport.ip öffnen und anmelden.",
                "Speedport-Modelle haben meist keine Gaming-Priorisierung; Gaming-PC per LAN-Kabel anschließen.",
                "Große Downloads am Gaming-Abend in DYNEX mit Profil „Turnier“ begrenzen lassen."]);
        if (id.Contains("vodafone") || id.Contains("arris") || id.Contains("technicolor") || id.Contains("sagem"))
            return new(name, [
                "http://192.168.0.1 öffnen und anmelden.",
                "Kabelrouter bieten selten QoS: Gaming-PC per LAN-Kabel verbinden.",
                "In DYNEX Profil „Turnier“ nutzen, damit Downloads der anderen PCs beim Spielen gedrosselt werden."]);
        if (id.Contains("asus"))
            return new(name, [
                "http://router.asus.com öffnen.",
                "Adaptive QoS → QoS-Typ „Adaptive QoS“ oder besser „Cake“/„Bandbreitenbegrenzer“ wählen und die gemessene Geschwindigkeit eintragen.",
                "Unter „Gaming“ den Spiele-PC als Gerät mit höchster Priorität setzen."]);
        if (id.Contains("tp-link") || id.Contains("tplink") || id.Contains("archer") || id.Contains("deco"))
            return new(name, [
                "http://tplinkwifi.net bzw. Deco-App öffnen.",
                "Erweitert → QoS aktivieren, Upload/Download auf etwa 90 % der gemessenen Werte setzen.",
                "Den Gaming-PC als Gerät mit hoher Priorität hinzufügen."]);
        if (id.Contains("netgear") || id.Contains("nighthawk") || id.Contains("orbi"))
            return new(name, [
                "http://routerlogin.net öffnen.",
                "Erweitert → Setup → QoS: „Dynamische QoS“ aktivieren und Speedtest im Router ausführen.",
                "Gaming-PC unter Geräteprioritäten auf „Höchste“ setzen."]);
        if (id.Contains("ubiquiti") || id.Contains("unifi"))
            return new(name, [
                "UniFi-Konsole öffnen → Einstellungen → Internet → WAN.",
                "„Smart Queues“ aktivieren und etwa 90 % der gemessenen Geschwindigkeit eintragen."]);
        if (id.Contains("openwrt") || id.Contains("linux"))
            return new(name, [
                "LuCI öffnen, Paket „luci-app-sqm“ installieren.",
                "Netzwerk → SQM QoS: Schnittstelle WAN, Qdisc „cake“, Skript „piece_of_cake.qos“, 90 % der gemessenen Werte."]);
        return new(name, [
            "Weboberfläche des Routers öffnen (Adresse steht meist auf dem Gerät).",
            "Nach „QoS“, „Smart Queue“, „SQM“ oder „Priorisierung“ suchen und den Gaming-PC bevorzugen.",
            "Falls nicht vorhanden: Gaming-PC per LAN-Kabel verbinden und DYNEX-Profil „Turnier“ nutzen."]);
    }
}
