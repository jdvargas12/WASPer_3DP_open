using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using QRCoder;

namespace WASPer_3DP.Components._1_2_Studies
{
    // M6 (Mobile and QR Connection, added 2026-08-19): lets someone open the live web viewer
    // from a phone or tablet on the same network by scanning a QR code, rather than typing a
    // LAN IP address by hand. The server itself (WASPer.XR.WebViewer's Program.cs) already
    // binds 0.0.0.0 so it accepts LAN connections -- this file is purely about telling the
    // *user* what address to point a phone at.
    public sealed partial class wsp_Sm01_WASPer_Study_Manager
    {
        // One row of the Mobile Access section: which network adapter it came from, the full
        // URL, and its rendered QR code. Nested here (rather than in the form-side Tab file)
        // because QR generation needs QRCoder, which only this file references.
        private readonly struct MobileAccessLink
        {
            public MobileAccessLink(string label, string url, Bitmap qr)
            {
                Label = label;
                Url = url;
                Qr = qr;
            }

            public string Label { get; }
            public string Url { get; }
            public Bitmap Qr { get; }
        }

        // Regenerates the QR codes/URLs shown in the Process Viewer tab's "Mobile Access"
        // section. Called once when the manager window opens (ShowManager) -- a machine's LAN
        // addresses essentially never change mid-session, so there is no need to recompute this
        // on every solve the way the live push/viewer-status polling do.
        //
        // Lists every candidate address rather than picking just one (2026-08-19): this machine
        // can be on more than one usable network at a time -- an institutional Wi-Fi that
        // happens to block phone-to-PC traffic (AP/client isolation, not fixable from here) plus
        // a Windows Mobile Hotspot created specifically to work around that -- and there is no
        // reliable way from inside the process to know which one a given phone can actually
        // reach. Showing all of them lets the user pick the one that matches whatever network
        // the phone is joined to that day, instead of only ever guessing one and being wrong.
        private void RefreshMobileAccess()
        {
            List<(string Label, string Ip)> candidates = ResolveLanIPv4Candidates();
            if (candidates.Count == 0)
            {
                _form?.UpdateMobileAccess(
                    Array.Empty<MobileAccessLink>(),
                    "Could not determine a LAN address for this computer -- connect it to " +
                    "Wi-Fi or Ethernet, then reopen the Study Manager.");
                return;
            }

            var links = new List<MobileAccessLink>();
            foreach ((string label, string ip) in candidates)
            {
                string url = BuildViewerUrl($"http://{ip}:5252/");
                Bitmap qr = null;
                try
                {
                    using var generator = new QRCodeGenerator();
                    using QRCodeData data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
                    using var renderer = new QRCode(data);
                    // Ownership of this Bitmap passes to the form (KpiManagerForm.
                    // UpdateMobileAccess takes over disposing it, same pattern as the existing
                    // snapshot-preview image) -- nothing here keeps its own reference to dispose
                    // later.
                    qr = renderer.GetGraphic(5);
                }
                catch
                {
                    // Left null -- the form shows the label/URL/Copy button without a QR image
                    // rather than dropping the whole entry, so a single bad adapter doesn't hide
                    // an otherwise-usable address.
                }
                links.Add(new MobileAccessLink(label, url, qr));
            }

            _form?.UpdateMobileAccess(
                links,
                "Scan whichever one matches the phone's current network, then click \"Open Web " +
                "Viewer\" here so it has something live to show. If the phone can't reach any of " +
                "these (e.g. an institutional Wi-Fi that blocks device-to-device traffic), " +
                "connect the phone to this computer's own Windows Mobile Hotspot instead and use " +
                "that address.");
        }

        // Best-effort, not a guarantee: every "Up", non-loopback/non-tunnel adapter's IPv4
        // address(es), Ethernet/Wi-Fi adapters listed first (ahead of VPN/virtual-switch ones --
        // Hyper-V, VMware, Docker, the Mobile Hotspot's own virtual adapter, etc. -- which are
        // also frequently reported as "Up"), but nothing is filtered out entirely: a hotspot
        // adapter is exactly the useful case 2026-08-19's AP-isolation workaround needs. Adapter
        // .Name is used as the label since .NET has no straightforward way to read the Wi-Fi
        // SSID from here -- it is at least usually distinguishable ("Wi-Fi" vs. "Local Area
        // Connection* N" for the hotspot), if not as friendly as an SSID would be.
        private static List<(string Label, string Ip)> ResolveLanIPv4Candidates()
        {
            try
            {
                List<NetworkInterface> candidates = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                    .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .ToList();

                IEnumerable<NetworkInterface> ranked = candidates
                    .Where(nic => nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    .Concat(candidates)
                    .Distinct();

                var result = new List<(string, string)>();
                var seenIps = new HashSet<string>();
                foreach (NetworkInterface nic in ranked)
                {
                    foreach (UnicastIPAddressInformation address in
                        nic.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;
                        string ip = address.Address.ToString();
                        if (!seenIps.Add(ip))
                            continue;
                        result.Add((nic.Name, ip));
                    }
                }
                return result;
            }
            catch
            {
                return new List<(string, string)>();
            }
        }
    }
}
