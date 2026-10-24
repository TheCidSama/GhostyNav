<p align="center">
  <img src="docs/ghosty.png" alt="The GhostyNav ghost" width="96" height="88">
</p>

<h1 align="center">GhostyNav</h1>

<p align="center">
  <b>A notch for your Windows desktop, with a little ghost who listens.</b><br>
  Media, controls and widgets that drop down from the top of your screen,
  and a voice assistant you can talk to while you point at things.
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-a78bfa" alt="MIT license"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" alt=".NET 10">
  <img src="https://img.shields.io/badge/AI-Gemini-8E75B2?logo=googlegemini&logoColor=white" alt="Powered by Gemini">
</p>

<p align="center">
  <img src="docs/ghost-thinking.png" alt="The GhostyNav notch at the top of the screen, the pixel ghost inside it thinking, with a Thinking card below" width="640">
</p>

---

GhostyNav puts a small black notch at the top centre of your screen, flush with the edge like a
MacBook's. It stays out of the way until you need it: click it and it springs open into a panel of
tabs; hover it while music plays and a mini player drops down. Hold <kbd>Ctrl</kbd>+<kbd>Alt</kbd> and the
notch opens around a pixel-art ghost that listens, watches what you circle on screen, and gets things
done.

It's a Windows desktop app written in C# on .NET 10 with no third-party packages, and it's built to
be light: a media player, control centre, calendar, system monitor, clipboard history, app launcher
and voice assistant in one notch, using **about 27 MB of memory when idle** and next to no CPU
([measurements](#light-on-your-pc)).

## Meet Ghosty

The ghost lives in the notch and shows you what it's doing.

<table>
  <tr>
    <td align="center"><img src="docs/mood-listening.png" alt="Listening" width="96" height="88"><br><sub><b>Listening</b><br>mouths along as you talk</sub></td>
    <td align="center"><img src="docs/mood-thinking.png" alt="Thinking" width="96" height="88"><br><sub><b>Thinking</b><br>glances around while Gemini works</sub></td>
    <td align="center"><img src="docs/mood-happy.png" alt="Happy" width="96" height="88"><br><sub><b>Done</b><br>throws its arms up</sub></td>
    <td align="center"><img src="docs/mood-sad.png" alt="Sad" width="96" height="88"><br><sub><b>Oops</b><br>when something didn't work</sub></td>
  </tr>
</table>

It blinks, its hem ripples, and it reads its replies aloud if you want it to. There are no image
files behind it: every frame is drawn from code, pixel by pixel.

## The voice assistant

1. **Hold <kbd>Ctrl</kbd>+<kbd>Alt</kbd>.** The notch drops down, the ghost starts listening and a soft
   blue glow rises along the edges of your screen, brightening with your voice. What you say appears
   under the notch as you say it.
2. **Point if you like.** While you hold the keys your cursor leaves a neon lavender trail. Draw a
   loop around anything on screen (a word, an error message, a photo, a chart) and that area goes
   along with your question. Going round the same thing again doesn't count twice.
3. **Let go.** Gemini works out what you meant and GhostyNav does it, or answers in the card and,
   if you turned it on, out loud.

Things you can say:

| You say | GhostyNav |
| --- | --- |
| "Open Spotify" | opens the app from your Start menu |
| "Search YouTube for lo-fi beats" | opens YouTube results in your browser |
| "Look up train times to Kyoto" | searches the web in your default browser |
| "Open github.com" | opens the site |
| *(circle a word)* "What does this mean?" | answers in a sentence or three |
| *(circle an error)* "How do I fix this?" | explains it, using what it sees |
| "Remind me to stretch every day at 3" | sets up a routine |
| "Open Spotify every weekday at 9" | sets up a routine that runs on its own |
| "What routines do I have?" / "Delete the stretch one" | reads or changes your routines |

**Circled something but not sure what to ask?** Let go without speaking and nothing is sent. The
area stays highlighted and the ghost waits; hold <kbd>Ctrl</kbd>+<kbd>Alt</kbd> again within a minute and
say what you want.

**Routines** are reminders and actions the assistant schedules for you: once, every day, on weekdays,
on chosen days, or every so many minutes. Reminders drop down from the notch (and are read aloud if
the voice is on); actions just happen. Every routine is listed in Settings with a delete button, so
nothing runs that you can't see.

## The notch

Click the notch to open it. It resizes to fit each tab, and you can switch tabs by clicking, by
dragging sideways, with a two-finger swipe on a touchpad, with the mouse wheel over the tab bar, or
from the keyboard.

| Tab | What's in it |
| --- | --- |
| **Controls** | Wi-Fi, Bluetooth, sound output (mute, or pick a device), Night Light, dark mode, volume, and the now-playing card, glowing in the colours of the cover, with a wavy, draggable timeline. Offers to open Spotify, if you have it, when nothing is playing. |
| **Today** | A big clock, the weather, this week at a glance, a timer with quick presets, and a to-do list. Ticking a to-do off splashes green. |
| **Stats** | Live CPU, GPU and RAM gauges that fill up as you open the tab, disk, network, uptime, the apps using the most RAM or CPU, and an internet speed test whose numbers count up as they're measured. |
| **Clipboard** | Your clipboard history (text, links, images, files) with search, filters and pins. Press <kbd>Enter</kbd> to paste an item straight into the app you were using. |
| **Apps** | Every real app in your Start menu with its own icon (no consoles, admin tools or uninstallers), fuzzy search, stars and recents, and a web search when nothing matches. |
| **Power** | Lock, sleep, restart and shut down. Restart and shut down ask for a second click. |
| **Settings** (the gear tile in Controls) | Your Gemini API key, the listening halo, spoken replies (off, female or male voice) and your routines. |

Even closed, the notch keeps you company:

- **Now playing:** album art and dancing audio bars in the colours of the cover, from Spotify or any
  app that reports to Windows' media controls.
- **Mini player:** hover the notch, or skip a track, and a player with the wavy timeline drops down,
  lit by the cover.
- **Timer:** a running countdown shows in the notch when no music is playing.
- **Reminders:** routines drop down when they're due.
- **Out of the way:** it hides itself while a game or video runs fullscreen, and never shows up in
  Alt+Tab or the taskbar.

Right-click the notch for **Start with Windows** and **Exit**.

## Shortcuts

| Keys | Does |
| --- | --- |
| Hold <kbd>Ctrl</kbd>+<kbd>Alt</kbd> | Talk to the assistant (circle things with the mouse while you hold) |
| <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Space</kbd> | Open Apps and start typing |
| <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>V</kbd> | Open the clipboard history |
| <kbd>Ctrl</kbd>+<kbd>1</kbd> ... <kbd>Ctrl</kbd>+<kbd>6</kbd> | Jump to a tab |
| <kbd>Ctrl</kbd>+<kbd>7</kbd> | Open Settings |
| <kbd>Ctrl</kbd>+<kbd>Tab</kbd>, <kbd>←</kbd> <kbd>→</kbd> | Next or previous tab |
| <kbd>Esc</kbd> | Close the notch |
| Any other key while holding <kbd>Ctrl</kbd>+<kbd>Alt</kbd> | Cancel the assistant |
| In Clipboard: <kbd>↑</kbd> <kbd>↓</kbd>, <kbd>Enter</kbd>, <kbd>Ctrl</kbd>+<kbd>C</kbd>, <kbd>Ctrl</kbd>+<kbd>P</kbd>, <kbd>Del</kbd>, <kbd>Tab</kbd> | Move, paste, copy, pin, remove, change filter |

The assistant only starts after a third of a second, so quick shortcuts that use
<kbd>Ctrl</kbd>+<kbd>Alt</kbd> (and typing with <kbd>AltGr</kbd>) aren't affected.

## Getting started

### You'll need

- 64-bit **Windows 11**, or Windows 10 version 2004 or later. It looks best on Windows 11, which has
  the Segoe Fluent Icons font.
- The **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** to build it.
- For the assistant: a **microphone** and a **Gemini API key**. A free key from
  [Google AI Studio](https://aistudio.google.com/apikey) works; the free tier has daily limits.

### Build and run

```powershell
git clone https://github.com/TheCidSama/GhostyNav.git
cd GhostyNav
dotnet run -c Release
```

Or build once with `dotnet build -c Release` and run
`bin\Release\net10.0-windows10.0.19041.0\GhostyNav.exe`. It shows up in Task Manager as GhostyNav,
with the ghost as its icon.

### Add your Gemini key

Click the notch, tap the gear tile in **Controls**, paste your key and press **Save**. GhostyNav checks
that it works, which doesn't use any of your quota. You can also set a `GEMINI_API_KEY` environment
variable instead; a key saved in Settings takes priority.

Then hold <kbd>Ctrl</kbd>+<kbd>Alt</kbd> and say hello.

## Privacy

GhostyNav has no accounts, no analytics and no telemetry. This is everything that leaves your PC, and when:

| When | Sent to | What |
| --- | --- | --- |
| While you hold <kbd>Ctrl</kbd>+<kbd>Alt</kbd> | Google Gemini | Your microphone audio, streamed for live transcription |
| When you let go after speaking | Google Gemini | What you said, any areas you circled (as images), the current time, your routines and the names of your installed apps |
| When you circle but don't speak | Nobody | Nothing is sent until you say what you want |
| When you click the weather | Open-Meteo | Your approximate location, rounded to about a kilometre |
| When you run the speed test | Cloudflare | Test data only |

Searches you ask for open in your own browser, on Google or YouTube.

The microphone is only on while you hold the keys. The assistant's own windows (the ghost, the card,
the trail and the glow) are hidden from screen capture, so they never end up in what's sent. Your data stays in
`%LOCALAPPDATA%\GhostyNav`. Your API key, your weather location and the speed test's city are
encrypted there with Windows' DPAPI, so only your Windows account on your PC can read them.

Two things worth knowing:

- **Clipboard history is stored as plain files.** Copies that password managers mark as private are
  skipped, but anything else you copy is kept until it scrolls out of the last 60 items (pinned items
  stay). Clear it from the Clipboard tab's options.
- **The assistant acts on what it hears and sees.** Text inside an area you circle, or a voice near
  your microphone, can steer what it does, and it doesn't ask before opening things. It can only open
  web links and apps from your Start menu, and routines are capped at 20 and can't repeat more often
  than every 5 minutes.

## Light on your PC

Everything above lives in a single small process. Here is what it uses:

| What GhostyNav is doing | Memory | CPU |
|---|---|---|
| Closed, waiting at the top of the screen | **27 MB** | 0.07 % |
| Open, moving between all seven pages | 45 to 58 MB | under 0.5 % with the live Stats gauges |
| Closed again after opening every page | 50 MB | 0.00 % |

<sub>Measured on Windows 11 with a Ryzen 5 8600G (12 threads), Release build. Memory is the figure
Task Manager shows in its Memory column (private working set); CPU is the share of the whole machine,
averaged over 15 to 30 seconds. The Apps page includes every Start menu icon and the Clipboard page
the saved history. Tools that show the full working set report around 110 MB when idle; most of that
is Windows and .NET libraries shared with other apps.</sub>

Why it stays small:

- **Memory:** each page is built the first time you open it, not at startup, and app icons are
  loaded once at the size they're shown.
- **Rendering:** when nothing on the notch is moving, nothing is redrawn. Animations run on demand
  at modest frame rates and stop as soon as they're done.
- **Polling:** the stats, the clock and the media position only update while they're on screen,
  and system counters are read off the UI thread so animations never wait for them.

## How it's built

- **WPF on .NET 10**, no third-party packages. It talks to Windows directly: media sessions, Core
  Audio, WinRT radios, performance counters, a low-level keyboard hook, the classic `waveIn`
  microphone API and DPAPI.
- **The notch** is a transparent, click-through window; only the black shape takes input. Its
  outline (flush top, flared shoulders, sloped sides) is rebuilt every frame from damped springs, so
  opening and closing stay smooth even when interrupted halfway.
- **The ghost** is a procedural 24×22 sprite, shaded and outlined in code and drawn with hard pixels.
  The app icon is rendered from the same sprite.
- **The neon trail** is a centripetal Catmull-Rom spline drawn as one ribbon per glow layer, so it
  has no visible joints and tapers into a soft tail.
- **The assistant** streams your voice to the Gemini Live API (`gemini-3.5-transcribe-live`) over a
  WebSocket, then asks `gemini-3.5-flash` (falling back to `gemini-3.5-flash-lite`) what to do,
  using function calling with thinking kept to a minimum. Replies are spoken with the voices built
  into Windows.

```
GhostyNav/
├── App.xaml / .cs          app start, theme and shared styles
├── MainWindow.xaml / .cs   the notch: shape, springs, tabs, peeks and the assistant's look
├── Controls/               ghost sprite and mascot, media card, wavy timeline, effects
├── Pages/                  one file pair per tab
├── Services/               media, audio, radios, clipboard, apps, stats, weather, storage
│   └── Assistant/          hotkey, microphone, live transcription, Gemini, routines, trail, voice
└── Assets/GhostyNav.ico    the ghost, as the app icon
```

## Good to know

- The notch, the trail and the halo live on your main monitor.
- Windows keeps normal apps from seeing keys pressed in apps running as administrator, so
  <kbd>Ctrl</kbd>+<kbd>Alt</kbd> may not reach the assistant while one of those is focused.
- Night Light can only be toggled once it has been turned on in Windows Settings at least once.
- The interface is in English.

## Contributing

Issues and pull requests are welcome. Please keep GhostyNav light: no heavy dependencies, and nothing
that runs or redraws while the notch is idle. `dotnet build -c Release` is all you need to build.

## License

GhostyNav is released under the [MIT License](LICENSE).

The Night Light toggle uses the registry format worked out by
[nightlight-cli](https://github.com/nathanbabcock/nightlight-cli), and weather comes from
[Open-Meteo](https://open-meteo.com/). GhostyNav is an independent project and isn't affiliated with
Apple, Google, Microsoft or Spotify.
