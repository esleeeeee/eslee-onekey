# eslee OneKey

eslee OneKey is a Windows app that launches your game, switches the audio output, joins a Discord voice channel, and swaps your game account — all from a single hotkey. When the game exits it restores the previous audio device, waiting until your call ends if you are still talking.

Documentation: [한국어](README.md) · **English**

## Download the latest build

Open the [latest GitHub Release](https://github.com/esleeeeee/eslee-onekey/releases/latest) and download the file whose name ends in `-Portable.zip`.

`Source code (zip)` and `Source code (tar.gz)` are developer archives, not the app. Most users should extract `-Portable.zip` and run `Eslee.OneKey.App.exe`.

No separate .NET installation is required. The app runs on Windows 10 2004 or later, or Windows 11 (x64).

## What you can do with it

- Get your game and voice setup ready with a single hotkey
- Switch to your headset when the game starts and back when it exits
- Hold the audio device while you are still in a Discord call, and restore it once the call ends
- Join a chosen Discord voice channel automatically
- Start with a different game account per hotkey
- Swap accounts while an automation is already running

## Getting started

The basic flow:

> Extract → Run → New automation → Name and hotkey → Program and watched process → Audio device → Discord connection and channel → Save

1. Extract the zip anywhere and run `Eslee.OneKey.App.exe`.
2. On the **Automation** screen, click **+ New automation** on the left.
3. Under **Basic**, set a name and a hotkey. Pick a combination no other program uses, such as `Ctrl + Alt + Shift + V`.
4. Turn on **Program** and choose the executable to launch. In **Watched program**, enter the process whose exit means the automation is over.
5. Turn on **Audio**, pick the output device to use, then choose whether to keep the current device or restore the previous one when the automation ends.
6. Turn on **Discord** and click **Reconnect Discord**. Once connected, turn on **Auto-join voice channel** and pick a server and channel from the lists.
7. Click **Save this automation**.

Hover the `?` next to a setting to see what it does.

## Using multiple game accounts

Create one automation per account and give each a different hotkey — for example `Ctrl + Alt + Shift + V` for one account and `Ctrl + Alt + Shift + A` for another.

To register accounts:

1. Sign in to the game launcher with the first account, with **stay signed in** enabled.
2. In the automation for that account, turn on **Account sign-in** and click **Register the account signed in now**.
3. Click **Open the sign-in screen for another account**. The launcher closes and reopens at the sign-in screen.
4. Sign in with the second account.
5. In the second automation, click **Register the account signed in now**.

After that, the hotkey alone starts the automation with the chosen account.

- Pressing another account's hotkey while a game is running swaps only the account. Audio and your ongoing call are left alone.
- Pressing the same account's hotkey again does nothing.
- Accounts are not swapped while the game is running. Close the game first.

> **Do not sign out from inside the launcher.** Signing out clears the sign-in data held on the server, which also invalidates the sessions you already saved for your other accounts — you would have to register them all again. Use **Open the sign-in screen for another account** instead.

## Audio after the automation ends

Choose what happens when the automation ends:

- **Keep the current device** — leave the audio device as it is.
- **Restore the device used before** — switch back to whatever was default before the automation started.

If you choose to restore, you can also enable **Wait for the Discord call to end before restoring**. If you close the game while still in a call, the headset stays until the call ends, then the device is restored automatically. Closing Discord entirely also counts as not being in a call.

While an automation is running or waiting to restore, **Restore the original device now** and **Keep the current device and stop** appear at the top of the window for when you want to decide immediately.

## Choosing a Discord server and channel

The server list shows **only servers you are in that the OneKey bot has also joined**. If the list is empty, invite the bot to that server and click **Refresh lists**.

The channel list shows only voice channels you can see. If you need a channel that is not listed, expand **When it is not in the list** and paste a channel link or ID.

## Tray

Closing the window keeps the app in the tray so hotkeys keep working. Right-click the tray icon to open the window, pause automations, check the current state, or exit.

If an [eslee Tray Folder](https://github.com/esleeeeee/eslee-tray-folder) host is running on the same PC, OneKey hides its own tray icon and works from the host's tray instead. It restores its own icon when the host is not there.

## Good to know

- Each automation has one hotkey, and two automations cannot share the same combination. A combination another program already registered will not work either.
- A disabled automation does not register its hotkey.
- Account switching works with sign-in data the launcher already stores. It never asks for a username or password, and it does not touch game files or anti-cheat drivers.
- If the Discord connection is refused, your account is not yet registered as an OneKey test user. Ask the app administrator to add it.
- A voice channel can appear in the list and still refuse you if you lack permission to connect.

## How it works

Pressing the hotkey runs these steps in order:

1. If an account is configured, check which account is signed in and swap in the saved session if it differs.
2. Start the executable. Nothing is launched if it is already running.
3. Remember the current default output device, then switch to the configured one.
4. Get Discord ready and join the configured voice channel.
5. Watch until the watched program exits.

Account switching swaps the sign-in session file the launcher keeps in your user folder, storing one copy per account. The launcher refreshes that data on every sign-in, so OneKey collects the current account's latest copy before writing the target account's copy in.

Call state is read from the Discord client on this PC — no user identity leaves the machine. Only the server list involves the bot, and even then OneKey sends the server IDs it already knows and receives back only the ones the bot also belongs to. The bot never reports its own server list, so it cannot reveal a server you did not already know about.

## Troubleshooting

**Nothing happens when I press the hotkey.**
Check that the automation is enabled — a cleared checkbox in the list means its hotkey is not registered. Also look for a hotkey registration warning at the top of the window; if another program owns the same combination, pick a different one.

**The account does not switch and I get the sign-in screen.**
The saved sign-in data is no longer valid, and the account status reads **Needs re-registration**. Sign in with that account again and click **Register the account signed in now**. Signing out from inside the launcher causes this.

**Discord status says "Please start Discord".**
Check that Discord is running. If it is and the status does not change, restart Discord and click **Reconnect Discord**.

**The server list is empty.**
A server only appears if the OneKey bot has joined it. Invite the bot, then click **Refresh lists**. If you just invited it, wait a moment and try again.

**I closed the game but the audio did not switch back.**
If you are still in a Discord call, OneKey waits until the call ends. To restore immediately, click **Restore the original device now** at the top of the window.

**I changed the audio device myself during an automation.**
Your choice wins. Automatic restoration is cancelled and the current device stays.

## Privacy and local data

Everything is stored on this PC only, under `%LOCALAPPDATA%\eslee OneKey`.

- Saved game account sessions and Discord tokens are encrypted with Windows DPAPI in separate files. Only the current Windows user can decrypt them, and they never appear in settings files or logs.
- Usernames and passwords are never requested or stored.
- Call state is read from the local Discord client, so no user identity is sent anywhere.
- The only thing sent to the bot is the list of server IDs you already know; the reply contains only the ones the bot also belongs to.

## Building from source

```powershell
dotnet restore eslee-onekey.slnx --configfile NuGet.Config
dotnet build eslee-onekey.slnx -c Release --no-restore
dotnet test tests/Eslee.OneKey.Tests/Eslee.OneKey.Tests.csproj -c Release --no-build
dotnet publish src/Eslee.OneKey.App/Eslee.OneKey.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -o artifacts/publish/win-x64 --configfile NuGet.Config
```

The executable is produced at `artifacts/publish/win-x64/Eslee.OneKey.App.exe`. `artifacts/` is excluded from Git.

Repository layout:

```text
src/Eslee.OneKey.Core                    Automation state machine and platform-independent contracts
src/Eslee.OneKey.Infrastructure.Windows  Audio, processes, global hotkeys, DPAPI, Discord RPC
src/Eslee.OneKey.App                     WPF window and tray
tests/Eslee.OneKey.Tests                 Automated tests
docs                                     Requirements, architecture, decisions, API contract, test plan
```

See [Architecture](docs/ARCHITECTURE.md), [API contract](docs/API_CONTRACT.md), and [Manual test plan](docs/MANUAL_TEST_PLAN.md) for details.

## Known limitations

- Changing the Windows default audio device has no public API, so the app uses the `PolicyConfig` COM interface. Each Windows update warrants a check on real hardware.
- Discord only reports the voice channels you can see, not whether you may connect, so a channel you lack permission for is refused at join time.
- Automatic updates, code signing, and an installer are not provided yet.

## License

[MIT License](LICENSE)
