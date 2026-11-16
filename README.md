# Phone Mirror

A Windows app that shows your Android phone's screen on your laptop and lets you control it with your mouse and keyboard, over USB or Wi-Fi.

Built on [scrcpy](https://github.com/Genymobile/scrcpy). Nothing needs to be installed on the phone.

## Download

Get **PhoneMirror.exe** from the [latest release](../../releases/latest) and double-click it. It's a single file with no installer.
If SmartScreen warns you, click **More info → Run anyway**.

The first time it runs, it downloads the mirroring engine (scrcpy, ~10 MB) to `%LOCALAPPDATA%\PhoneMirror`.

## One-time phone setup

1. On the phone, open **Settings → About phone** (Samsung: **About phone → Software information**).
2. Tap **Build number** 7 times until it says you're a developer.
3. Go to **Settings → System → Developer options** and turn on **USB debugging**.
4. Plug the phone into the laptop with a data cable, unlock it, and tap **Allow** (tick *Always allow from this computer*).
5. The phone appears in Phone Mirror. Click **Start Mirroring**.

## Features

- **Opens by itself** when you plug in your phone (phones are detected instantly)
- **Goes wireless automatically**: plugging in also turns on Wi-Fi mode, so you can unplug and the mirror switches to Wi-Fi
- **Add phone wirelessly (QR code)**: scan a code with Android 11+ Wireless debugging, with nothing to type
- **Auto quality**: sharp over USB, low-lag over Wi-Fi
- **Start with Windows**: runs quietly in the tray, so plugging in your phone just works
- Offers to add itself to the Start menu and desktop, and **updates itself** from this repo's releases
- **Looks like a phone**: the mirror sits inside a phone body (black, silver, or blue) with a button strip for power, volume, back, home, recents, screenshot and keep-on-top. Drag the bezel to move it, drag the bottom-right corner to resize it, and it rotates with your phone. Choose **Look: Plain window** for the classic window.
- Mirror and control over **USB** or **Wi-Fi**
- Phone audio on the laptop (Android 11+)
- Turn the phone screen off while mirroring
- Record to MP4 (`Videos\Phone Mirror`) and take screenshots (`Pictures\Phone Mirror`)
- Always-on-top, full screen, and quality settings
- Drag an `.apk` onto the phone window to install it, or drag any other file to copy it to the phone's Download folder

### Shortcuts in the phone window

| Action | Shortcut |
|---|---|
| Back | Right-click |
| Home | Middle-click |
| Full screen | Alt+F |
| Rotate | Alt+R |
| Phone screen off | Alt+O |

## Building

Every push to `main` builds the app on GitHub Actions and publishes a new release.
To build locally: `dotnet publish src/PhoneMirror.csproj -c Release -o out` (needs the .NET 8 SDK).
