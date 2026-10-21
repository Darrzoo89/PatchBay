# PatchBay

Patch cables for your PC's audio. Like aux cords, but on screen: each audio device is a box with
jacks, and you drag a cable from one jack to another to send sound there. A simpler, visual take on VoiceMeeter.

## Launch

Double-click `dist\PatchBay.exe` (needs the .NET 8 Desktop Runtime, which is already on this PC).

To rebuild after changing code:

```
dotnet publish src\PatchBay -c Release -o dist
```

PatchBay only moves sound while it's open, so leave it running (minimised is fine). Your cables,
box positions and settings are saved to `%APPDATA%\PatchBay\layout.json` and come back on restart.

## Using it

- **OUT jacks** (right side of a box) = sound coming out of a device: a mic, your Focusrite's inputs, "CABLE Output".
- **IN jacks** (left side) = where sound goes in: speakers, headphones, "CABLE Input".
- Drag from a jack to another jack to plug in a cable. One OUT can feed many INs; many cables can mix into one IN.
- Click a cable to unplug it (**Ctrl+Z** puts it back). Scroll over a cable for volume. Right-click for mute, volume, channels and colour.
- **Channels → Left only** sends input 1 of a stereo interface to both sides (good for a guitar in input 1).
- **Buffer** (toolbar) sets the delay. Default is 3 ms, the lowest; raise it one step if you hear crackles.
- Unplugged devices stay as greyed boxes; their cables reconnect automatically when the device comes back.
- "Show what's playing jacks" adds an OUT jack to every speaker so you can grab whatever is playing on it.

## What to install

**VB-Audio Virtual Cable** (free): <https://vb-audio.com/Cable/>. Unzip, right-click `VBCABLE_Setup_x64.exe` →
Run as administrator → Install Driver, then restart. It's a fake sound card that loops whatever goes IN back OUT,
which is how apps get chained together. (This PC already has the 16-channel version, shown as "CABLE In 16ch".)

Voicemod installs its own virtual device ("Voicemod Virtual Audio Device"), so nothing extra is needed for it.

## Guitar → Focusrite → Voicemod → FL Studio

1. Plug the guitar into Focusrite input 1.
2. In PatchBay, drag a cable from the Focusrite box's OUT jack (e.g. "Analogue 1 + 2") to the VB-Audio box's IN jack ("CABLE Input" / "CABLE In 16ch").
3. Right-click that cable → Channels → Left only.
4. Voicemod → Settings: Microphone = "CABLE Output (VB-Audio Virtual Cable)". Voicemod sends its result to "Voicemod Virtual Audio Device".
5. FL Studio → Options → Audio settings: device "FL Studio ASIO"; in its panel set Input = "Microphone (Voicemod Virtual Audio Device)" and Output = your Focusrite. Pick that input on a mixer track.
6. Optional: to hear Voicemod without FL Studio, cable Voicemod's OUT jack to your Focusrite/headphones IN jack.

Don't use "Focusrite USB ASIO" in FL Studio for this chain: ASIO can only record from the Focusrite itself.

## About delay

PatchBay uses Windows shared-mode WASAPI so other apps can still use the same devices. Each hop through a
virtual cable and through Voicemod adds some delay of its own. For live playing, turn on the Focusrite's Direct
Monitor to hear the dry guitar with zero delay.

Measured on this PC (`tests\EngineCheck`): audio passes at full level with no dropouts at a 3 ms buffer.

## If something's wrong

- Cable dashed with ⚠: hover it for the reason (usually another app holds the device in exclusive mode). Close that app, then press **Refresh devices**.
- Echo/feedback: you've made a loop. Unplug one cable in it.
- Errors are logged to `%APPDATA%\PatchBay\log.txt`.

## Project layout

- `src/PatchBay/Audio` — device list, routing engine (WASAPI capture → per-cable buffer → mixer → WASAPI output).
- `src/PatchBay/Ui` — boxes, jacks, cables, setup guide.
- `tests/EngineCheck` — sends a quiet test tone through a cable between virtual devices and checks it arrives cleanly: `dotnet run --project tests\EngineCheck -- 3`.
