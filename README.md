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
  <a href="https://github.com/TheCidSama/GhostyNav/releases/latest/download/GhostyNav-Setup.exe"><img src="https://img.shields.io/badge/Download-GhostyNav--Setup.exe-A78BFA?style=for-the-badge" alt="Download GhostyNav-Setup.exe"></a>
</p>

<p align="center">
  <a href="https://github.com/TheCidSama/GhostyNav/releases/latest"><img src="https://img.shields.io/github/v/release/TheCidSama/GhostyNav?color=a78bfa" alt="Latest release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-a78bfa" alt="MIT license"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/RAM-~24%20MB%20idle-2ea44f" alt="About 24 MB of RAM when idle">
  <img src="https://img.shields.io/badge/AI-Gemini-8E75B2?logo=googlegemini&logoColor=white" alt="Powered by Gemini">
</p>

<p align="center">
  <img src="docs/ghost-thinking.png" alt="The GhostyNav notch at the top of the screen, the pixel ghost inside it thinking, with a Thinking card below" width="640">
</p>

---

GhostyNav puts a small black notch at the top centre of your screen, flush with the edge like a
MacBook's. Click it and it springs open into a panel of tabs. Hover it while music plays and a mini
player drops down. Press the volume keys and a slim bar slides out. Drop a file on it to keep it for
later. Hold <kbd>Ctrl</kbd>+<kbd>Alt</kbd> and a pixel-art ghost listens, looks at whatever you circle on
screen, and gets things done. All of that in about 24 MB of memory.

## Highlights

- **Talk to it.** Hold <kbd>Ctrl</kbd>+<kbd>Alt</kbd>, speak, let go. It opens apps, searches the web,
  plays and pauses music, sets the volume, flips Wi-Fi or dark mode, starts timers, keeps your to-dos,
  answers questions and types for you, out loud if you like.
- **Point at things.** Circle anything on screen with a neon trail and ask about it.
- **A notch that keeps you posted.** Volume and brightness bars, notifications, your next meeting,
  a light when an app uses your microphone or camera, and a hello when your earbuds connect.
- **Drop shelf.** Drag files onto the notch to park them, drag them out wherever they're needed.
- **Control centre.** Wi-Fi, Bluetooth, sound output, Night Light, dark mode and volume, one click away.
- **Now playing.** Album art, a glow in the colours of the cover and a wavy timeline you can drag,
  from Spotify or any media app.
- **Your day.** Clock, weather, the week and your calendar, a timer with focus sessions, and a to-do list.
- **System stats.** Live CPU, GPU and RAM gauges, the busiest apps and an internet speed test.
- **Clipboard history.** Text, links, images and files, encrypted, pasted back with <kbd>Enter</kbd>.
- **App launcher.** Fuzzy search over everything in your Start menu.
- **Yours.** Pick the notch's size, its accent colour, its screen, its tabs and your talk keys.
- **Light.** About 24 MB of memory and no CPU to speak of while it waits.

## Install

1. Download **[GhostyNav-Setup.exe](https://github.com/TheCidSama/GhostyNav/releases/latest/download/GhostyNav-Setup.exe)**
   from the [latest release](https://github.com/TheCidSama/GhostyNav/releases/latest).
2. Run it and click **Install**. Setup:
   - installs GhostyNav just for you, with no admin rights needed
   - adds it to the Start menu and to your **PATH**, so typing `ghostynav` in any new terminal starts it
   - can start it when you sign in and add a desktop shortcut, if you leave those boxes ticked
   - gets the free **.NET 10 Desktop Runtime** from Microsoft if your PC doesn't have it yet
     (Windows asks for permission once)
3. Click **Open GhostyNav**. The notch appears at the top of your screen.

The installer isn't code-signed yet, so Windows SmartScreen may say it protected your PC. Click
**More info**, then **Run anyway**.

**Updates** arrive by themselves: GhostyNav looks for a new version once a day and tells you in the
notch; one click in **Settings › About** installs it and keeps your settings. To **uninstall**, go to
**Settings › Apps › Installed apps › GhostyNav › Uninstall**. It removes the app, its shortcuts and its
PATH entry, and only deletes your settings if you ask it to.

<details>
<summary>Installing from a script</summary>

| Command | Does |
| --- | --- |
| `GhostyNav-Setup.exe /quiet` | Installs without a window, with the default choices |
| `/nostartup`, `/nopath`, `/desktop`, `/launch` | Added after `/quiet`: don't start at sign-in, don't add to PATH, add a desktop shortcut, open it when done |
| `"%LOCALAPPDATA%\Programs\GhostyNav\uninstall.exe" /quiet` | Uninstalls without a window (add `/purge` to delete settings too) |

</details>

**You'll need** 64-bit Windows 11, or Windows 10 version 2004 or later. For the assistant, a
microphone and a free Gemini API key.

### Add your Gemini key

1. Get a free key from [Google AI Studio](https://aistudio.google.com/apikey).
2. Click the notch, tap the gear tile in **Controls**, paste the key and press **Save**. GhostyNav
   checks that it works without using any of your quota.
3. Hold <kbd>Ctrl</kbd>+<kbd>Alt</kbd> and say hello.

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

It blinks, its hem ripples, and it reads its replies aloud in a female or male voice if you want it to.

## The voice assistant

1. **Hold <kbd>Ctrl</kbd>+<kbd>Alt</kbd>.** The notch drops down, the ghost starts listening and a soft
   blue glow rises along the edges of your screen, brightening with your voice. Your words appear
   under the notch as you say them.
2. **Point if you like.** While you hold the keys, your cursor leaves a neon lavender trail. Loop it
   around anything on screen (a word, an error message, a photo, a chart) and that area goes along
   with your question.
3. **Let go.** GhostyNav does what you asked, or answers in a card with a **Copy** button that tidies
   itself away after a few seconds.

| You say | GhostyNav |
| --- | --- |
| "Open Spotify" | opens the app |
| "Pause the music" / "Next song" | controls whatever is playing |
| "Volume to 30" / "Mute" | sets the sound |
| "Turn on dark mode and switch off Bluetooth" | flips Wi-Fi, Bluetooth, dark mode or Night Light |
| "Set a timer for 10 minutes" / "Start a focus session" | runs the timer, or 25-minute focus rounds with breaks |
| "Add milk to my to-dos" / "I bought the milk" | adds and ticks off to-dos |
| "What's my next meeting?" | reads your calendar |
| "Search YouTube for lo-fi beats" / "Look up train times to Kyoto" | searches in your browser |
| *(circle a word)* "What does this mean?" | explains it in a sentence or three |
| *(circle an error)* "How do I fix this?" | tells you, using what it sees |
| "Write a note to Sam saying I'll be late" | types it into the app you're using |
| "Copy that" | copies its last answer |
| "Remind me to stretch every day at 3" | sets up a reminder |
| "Open Spotify every weekday at 9" | sets up a routine that runs on its own |

**It remembers the conversation** for a few minutes, so "and tomorrow?" or "copy that" follow on
from what you just asked.

**Circled something but not sure what to ask?** Let go without speaking. The area stays highlighted
and the ghost waits; hold the keys again within a minute and say what you want.

**It checks before acting on what it reads.** When a request came with a circled area and would open
a website or type text, the card asks first, with **Go ahead** and **Cancel**.

**Routines** are reminders and actions the assistant schedules for you: once, every day, on weekdays,
on chosen days, or every few minutes. You can see and delete every routine in Settings.

**Prefer other keys?** Choose <kbd>Ctrl</kbd>+<kbd>Win</kbd> or <kbd>Alt</kbd>+<kbd>Win</kbd> in Settings.

## The notch

### Closed

- **Now playing:** album art and dancing bars in the colours of the cover.
- **Mini player:** hover the notch, or skip a track, and a player drops down.
- **Volume and brightness:** a slim bar slides out of the notch when you change them.
- **Notifications:** new Windows notifications drop down from the notch.
- **Your calendar:** a heads-up five minutes before each meeting.
- **Microphone and camera light:** an orange dot while another app uses your microphone, a green one
  for the camera.
- **Charging and earbuds:** plugging in, low battery, and Bluetooth headphones connecting, with their
  battery level.
- **Timer and focus:** the countdown shows in the notch; breaks count down in green.
- **Drop shelf:** drag files onto the notch to keep them there. Hover to see them, drag them out into a
  folder, an email or a chat, double-click to open. The shelf only remembers where the files are, so it
  takes no space.
- **Reminders:** routines drop down when they're due.
- **Out of the way:** it steps aside for fullscreen games and videos, and stays out of Alt+Tab and the taskbar.

### Open

Click the notch to open it. It resizes to fit each tab, and you can switch tabs by clicking, by
dragging sideways, with a two-finger swipe, with the mouse wheel or from the keyboard.

| Tab | What's in it |
| --- | --- |
| **Controls** | Wi-Fi, Bluetooth, sound output, Night Light, dark mode and volume, plus the now-playing card glowing in the colours of the cover, with a wavy timeline you can drag. Offers to open Spotify when nothing is playing. |
| **Today** | A big clock, the weather, the week with your next calendar event, a timer with presets and focus sessions, and a to-do list that splashes green when you tick things off. |
| **Stats** | CPU, GPU and RAM gauges that fill up as you open the tab, disk, network, uptime, the apps using the most, and a speed test whose numbers count up live. |
| **Clipboard** | Your clipboard history (text, links, images and files) with search, filters and pins. <kbd>Enter</kbd> pastes straight into the app you were using. |
| **Apps** | Every app in your Start menu with its icon, fuzzy search, favourites and recents, and a web search when nothing matches. |
| **Power** | Lock, sleep, restart and shut down. Restart and shut down ask for a second click. |

The gear tile in **Controls** opens **Settings**:

- **Assistant:** your Gemini key, talk keys, the listening glow, the voice and your routines.
- **Notch:** its size, accent colour and screen, which moments it shows, and which tabs you keep.
- **About:** your version, updates, and a link here.

Right-click the notch to turn **Start with Windows** on or off, or to exit.

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
| In Clipboard: <kbd>↑</kbd> <kbd>↓</kbd>, <kbd>Enter</kbd>, <kbd>Ctrl</kbd>+<kbd>C</kbd>, <kbd>Ctrl</kbd>+<kbd>P</kbd>, <kbd>Del</kbd> | Move, paste, copy, pin, remove |

## Light on your PC

| What GhostyNav is doing | Memory | CPU |
|---|---|---|
| Closed, waiting at the top of the screen | **24 MB** | 0.03 % |
| Open, moving between all seven pages | 52 to 60 MB | under 0.4 % with the live Stats gauges |
| Closed again after opening every page | 59 MB | 0.1 % |

<sub>Measured on Windows 11 with a Ryzen 5 8600G. Memory is the figure in Task Manager's Memory
column; CPU is the share of the whole machine.</sub>

Pages are only built the first time you open them, nothing is redrawn while the notch is idle, and
stats only update while you're looking at them. Volume, devices, the microphone light and the shelf
all wait for Windows to tell them something changed rather than checking. Notifications are the
exception: Windows only announces them to Store apps, so GhostyNav takes a quick local look every
few seconds (switch it off in Settings if you don't need it).

## Privacy

No accounts, no analytics, no telemetry. This is everything that leaves your PC:

| When | Sent to | What |
| --- | --- | --- |
| While you hold the talk keys | Google Gemini | Your voice, for live transcription |
| When you let go after speaking | Google Gemini | What you said, any areas you circled, the time, your routines, to-dos and upcoming calendar events, the names of your apps, and your last few exchanges |
| When you click the weather | Open-Meteo | Your approximate location |
| When you run the speed test | Cloudflare | Test data only |
| Once a day, if updates are on | GitHub | A request for the latest version number |

Notifications, your calendar and the microphone light are read on your PC and never leave it. The
microphone is only on while you hold the keys, and if you let go without speaking, no question or
circled area is sent. Your API key, location and clipboard history are encrypted on your PC with
Windows' own protection, so only your Windows account can read them; copies that password managers
mark as private are never saved.

## Build from source

With the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

```powershell
git clone https://github.com/TheCidSama/GhostyNav.git
cd GhostyNav
dotnet run -c Release
```

To build the installer, run `powershell -ExecutionPolicy Bypass -File installer\build.ps1`. It
writes `publish\GhostyNav-Setup.exe`.

Issues and pull requests are welcome. Please keep GhostyNav light: no heavy dependencies, and nothing
that runs or redraws while the notch is idle.

## License

GhostyNav is released under the [MIT License](LICENSE).

The Night Light toggle uses the registry format worked out by
[nightlight-cli](https://github.com/nathanbabcock/nightlight-cli), and weather comes from
[Open-Meteo](https://open-meteo.com/). GhostyNav is an independent project and isn't affiliated with
Apple, Google, Microsoft or Spotify.
