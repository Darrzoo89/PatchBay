using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PatchBay.Ui;

/// <summary>The "Setup guide" side panel.</summary>
internal static class Guide
{
    public static void Fill(Panel panel)
    {
        panel.Children.Clear();
        H(panel, "How it works");
        P(panel, "Every audio device is a box. OUT jacks (right side) are sound coming out of a device — a mic, your Focusrite's inputs, \"CABLE Output\". IN jacks (left side) are where sound goes in — speakers, headphones, \"CABLE Input\".");
        P(panel, "Drag from one jack to another to plug in a cable. One OUT can feed many INs, and many cables can mix into one IN.");
        Bullets(panel,
            "Click a cable to unplug it (Ctrl+Z puts it back).",
            "Scroll over a cable to change its volume.",
            "Right-click a cable for mute, volume and channel options.",
            "Drag a box by its title. Right-click a title to hide a box.",
            "The little bar under each jack is its level meter.");
        P(panel, "PatchBay only moves sound while it's open, so leave it running (minimised is fine).");

        H(panel, "One-time install");
        P(panel, "To chain apps together you need a free virtual cable — a fake sound card that loops whatever goes IN back OUT:");
        Bullets(panel,
            "VB-Audio Virtual Cable (free): download, unzip, right-click VBCABLE_Setup_x64.exe → Run as administrator → Install Driver, then restart the PC.",
            "Voicemod already installs its own virtual device (\"Voicemod Virtual Audio Device\").");
        Link(panel, "Get VB-Audio Virtual Cable", "https://vb-audio.com/Cable/");
        P(panel, "Tip: in Windows Sound settings set the Focusrite, CABLE and Voicemod devices to the same rate (48000 Hz) for the cleanest sound. PatchBay converts if they differ.");

        H(panel, "Your chain: guitar → Focusrite → Voicemod → FL Studio");
        Step(panel, "1", "Plug the guitar into Focusrite input 1.");
        Step(panel, "2", "In PatchBay, drag a cable from the Focusrite box's OUT jack (e.g. \"Analogue 1 + 2\") to the VB-Audio box's IN jack \"CABLE Input\" (called \"CABLE In 16ch\" on the 16-channel version).");
        Step(panel, "3", "Right-click that cable → Channels → Left only (input 1 is the left channel), so the guitar comes out of both sides.");
        Step(panel, "4", "In Voicemod → Settings, set the Microphone/Input to \"CABLE Output (VB-Audio Virtual Cable)\". Voicemod sends its changed sound to \"Voicemod Virtual Audio Device\" automatically.");
        Step(panel, "5", "In FL Studio → Options → Audio settings, choose the \"FL Studio ASIO\" device. In its panel set Input to \"Microphone (Voicemod Virtual Audio Device)\" and Output to your Focusrite. Then pick that input on a mixer track.");
        Step(panel, "6", "Optional: to hear the effect without FL Studio, drag a cable from the Voicemod box's OUT jack to your Focusrite (or headphones) IN jack.");
        P(panel, "Avoid \"Focusrite USB ASIO\" in FL Studio for this chain: ASIO can only record from the Focusrite itself, not from Voicemod.");

        H(panel, "If something's wrong");
        Bullets(panel,
            "Crackles or dropouts: raise Buffer one step at a time (5, 10, 20 ms) until it's clean.",
            "Too much delay: lower Buffer, and turn on the Focusrite's Direct Monitor to hear the dry guitar instantly.",
            "A cable turns dashed with ⚠: hover it to see why. Usually another app has the device in exclusive mode — close it, then press Refresh devices.",
            "Echo or feedback: you've made a loop (e.g. a device feeding itself through a virtual cable). Unplug one cable in the loop.",
            "A device is missing: press Refresh devices, or check it's enabled in Windows Sound settings.");
    }

    private static void H(Panel p, string text) => p.Children.Add(new TextBlock
    {
        Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, p.Children.Count == 0 ? 0 : 18, 0, 6),
        TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White,
    });

    private static void P(Panel p, string text) => p.Children.Add(new TextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Foreground = Theme.Brush(0xC8, 0xCD, 0xD6), LineHeight = 19,
    });

    private static void Bullets(Panel p, params string[] items)
    {
        foreach (var item in items)
        {
            var g = new DockPanel { Margin = new Thickness(4, 0, 0, 6) };
            var dot = new TextBlock { Text = "•", Margin = new Thickness(0, 0, 8, 0), Foreground = Theme.Muted };
            DockPanel.SetDock(dot, Dock.Left);
            g.Children.Add(dot);
            g.Children.Add(new TextBlock { Text = item, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush(0xC8, 0xCD, 0xD6), LineHeight = 19 });
            p.Children.Add(g);
        }
    }

    private static void Step(Panel p, string n, string text)
    {
        var g = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var badge = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Theme.Brush(0x3F, 0xA7, 0xFF),
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = n, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White },
        };
        DockPanel.SetDock(badge, Dock.Left);
        g.Children.Add(badge);
        g.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush(0xC8, 0xCD, 0xD6), LineHeight = 19 });
        p.Children.Add(g);
    }

    private static void Link(Panel p, string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url), Foreground = Theme.Brush(0x6C, 0xBA, 0xFF) };
        link.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        };
        p.Children.Add(new TextBlock(link) { Margin = new Thickness(0, 0, 0, 10) });
    }
}
