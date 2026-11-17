# Remoter

[Русский](README.md) · **English**

A Telegram bot that turns your phone into a remote control for this PC: you send a task in Telegram, and the local [OpenCode](https://opencode.ai) runs it in the terminal. The bot shows progress, sends code and diffs, takes screenshots, and asks for confirmation before dangerous actions — and in `AUTO_APPROVE` mode it runs completely without questions.

```
Telegram  ──►  Remoter (WinForms, tray)  ──►  OpenCode service (127.0.0.1)  ──►  files / terminal
```

Remoter does nothing itself — it creates an OpenCode session, sends the prompt, listens to events, and turns permission requests into inline buttons.

---

## Requirements

- Windows 10/11, .NET 10 (SDK or Desktop Runtime).
- OpenCode installed and configured (the `opencode` command is available in PATH).
- A Telegram account and a bot token from [@BotFather](https://t.me/BotFather).
- A model with an available quota/balance in OpenCode.

## Quick start

### 1. Get a bot token

1. Open [@BotFather](https://t.me/BotFather) in Telegram.
2. Send `/newbot`.
3. Choose a name (for example, `My Remoter`) and a username ending in `bot`
   (for example, `my_remoter_bot`).
4. BotFather will send a string like `123456789:AA...` — that is the token.

> Never show the token to anyone and never commit it. It is stored only in `.env`.

### 2. Start Remoter and fill in `.env`

Start `Remoter.exe` (or `dotnet run --project src\Remoter.App`). On the first launch, a `.env` file appears next to the exe:

```dotenv
TELEGRAM_BOT_TOKEN=
ALLOWED_USER_IDS=
```

Paste the token:

```dotenv
TELEGRAM_BOT_TOKEN=123456789:AA...
```

### 3. Find your Telegram ID

Restart Remoter and send `/start` to the bot. While `ALLOWED_USER_IDS` is empty, the bot runs in setup mode: it replies only to your ID and does not execute anything. The greeting shows your ID and the menu buttons.

Copy the ID into `.env`:

```dotenv
ALLOWED_USER_IDS=123456789
```

You can list several IDs separated by commas. Restart Remoter — the bot is ready.

### 4. Test the chain without Telegram

```powershell
Remoter.exe --selftest
```

This checks: finding/starting the OpenCode service, creating a session, sending a prompt, the event stream, and the final response. The result is written to the console and the log. Exit code `0` means success.

---

## Commands

| Command | What it does |
| --- | --- |
| *(any text)* | A task for the agent in the current session |
| `/start` | Greeting, status, and the main menu |
| `/menu` | Main menu with buttons |
| `/help` | List of commands and the menu |
| `/id` | Your user/chat ID |
| `/new [path]` | New session; without a path, choose a folder with buttons |
| `/sessions` | Recent sessions, switch with buttons |
| `/use N` | Switch to a session from the list |
| `/status` | What is running now, the queue |
| `/cancel` | Interrupt the task and clear the queue |
| `/agent [build\|plan]` | Agent mode; without an argument, buttons |
| `/model [name]` | Choose a model (without an argument, buttons) |
| `/screen` | Screenshot of the screen (if the session is not locked) |
| `/file path` | Send a file or image |
| `/ls [path]` | File browser with buttons; a file is sent immediately |
| `/diff` | Changes in the current session |
| `/restart` | Restart the OpenCode service |
| `/shutdown` | Shut down Remoter |

While a task is running, new messages are queued. A long response arrives as a file, code as `<pre>` blocks, and progress is updated in a single message.

## Menu and buttons

You don't have to type commands on your phone: `/start`, `/menu`, or the **☰ Menu** button opens the inline menu. It contains files, a new session, sessions, model, agent, status, screenshot, diff, cancel, restart, and help.

The main actions are also duplicated on the **persistent keyboard at the bottom of the chat** — it stays above the input field, so you don't need to scroll to the menu message. If the keyboard is hidden, send `/start` to bring it back.

**Folder selection.** `/new` without an argument and the **📂 Files** item open a browser: first the drive buttons (`C:\`, `D:\`), then the actual folders and files on the selected drive. The **⬆️ Up**, **💾 Drives**, **🏠 Home**, **📁 Current**, and **🔄 Refresh** buttons help you move around quickly; **⬅️/➡️** page through long folders. Tap a folder to open it, or a file to receive it in the chat. The **🆕 New session here** button creates a session in the open folder.

Old messages with buttons don't break navigation: if the list is out of date, the bot asks you to open it again. The menu, model lists, agents, and sessions also switch with buttons.

Light requests (menu, file list, status) are handled immediately. Screenshots, file sending, and OpenCode restarts run in the background, so the bot keeps responding. On startup, Remoter brings OpenCode up in advance so the first request doesn't wait for a cold start.

## Custom models and agents

Remoter is not tied to a specific model — it works with whatever is configured in OpenCode. The model and agent are set in `appsettings.json`/`.env` and can be switched on the fly from Telegram:

```dotenv
MODEL_PROVIDER=opencode
MODEL_ID=deepseek-v4.1-flash-free
AGENT=build
```

- `/model` — list of models from OpenCode, choose with buttons;
- `/agent build` — agent with edits and commands, `/agent plan` — planning only;
- any custom agents and providers added to OpenCode also appear in the bot.

If a model runs out of quota, the bot shows the provider error — just choose another model via `/model`.

## Approvals

Without asking, only reading is allowed: `read`, `glob`, `grep`, and access to folders outside the project. File edits, terminal commands, network, web search, and subagents arrive with buttons:

- **✅ Allow** — once;
- **🔁 Always** — until the end of the session;
- **❌ Deny** — the action is not performed.

Reading `.env` also requires approval (except `.env.example`). If you don't press a button within `APPROVAL_TIMEOUT_SECONDS` (5 minutes by default), the action is denied.

### Working without approvals

If you don't need the buttons, enable this in `.env`:

```dotenv
AUTO_APPROVE=true
```

Then all actions (edits, terminal, network, subagents) run immediately, without questions. The `ALLOWED_USER_IDS` allowlist remains mandatory: without it, the bot listens to no one. The `question` tool is still disabled — Telegram cannot display its forms, so the agent asks questions as plain text.

## Autostart

```powershell
Remoter.exe --install-autostart     # add to HKCU\...\Run
Remoter.exe --uninstall-autostart   # remove
```

The same options are in the tray icon menu. Double-clicking the icon opens the logs folder.

## Sleep and PC shutdown

While Windows sleeps, it freezes all programs and disconnects the network, so the bot **cannot** receive messages or run tasks while the PC is asleep. This is a system limitation, not a bot limitation.

What happens after waking up:

- the bot reconnects to Telegram and OpenCode by itself (no restart needed);
- Telegram keeps messages sent during sleep for up to 24 hours — they arrive immediately after waking and are executed;
- if a task was interrupted by sleep, you can repeat it or cancel it via `/cancel`.

To keep the bot running around the clock, prevent the PC from sleeping:

```powershell
# in .env next to Remoter.exe
PREVENT_SLEEP=true
```

Or enable **"Prevent PC from sleeping"** in the tray menu. The screen may still turn off — that's fine, programs keep running.

For a laptop, also check the lid-close action: change "Sleep" to "Do nothing" (for AC power only).

If the PC does go to sleep, you can wake it remotely via Wake-on-LAN, but that requires another always-on computer or router — the bot itself does not run while asleep.

## Settings

The main values are in `appsettings.json` next to the exe. They can be overridden in `.env`:

| Variable | Default | Meaning |
| --- | --- | --- |
| `TELEGRAM_BOT_TOKEN` | — | Bot token (required) |
| `ALLOWED_USER_IDS` | — | Telegram ID allowlist (required) |
| `DEFAULT_DIRECTORY` | `~` | Default working folder (`~` is the home folder) |
| `MODEL_PROVIDER` | `opencode` | Model provider |
| `MODEL_ID` | `deepseek-v4.1-flash-free` | Model |
| `AGENT` | `build` | Default agent |
| `APPROVAL_TIMEOUT_SECONDS` | `300` | Approval timeout |
| `AUTO_START_OPENCODE` | `true` | Start the OpenCode service automatically |
| `OPENCODE_COMMAND` | `opencode.cmd` | How to invoke OpenCode |
| `PREVENT_SLEEP` | `false` | Prevent the PC from sleeping while the bot is running |
| `AUTO_APPROVE` | `false` | Don't ask for approval of dangerous actions |

## Where the data lives

- Settings: `.env` next to `Remoter.exe` (or in the project root when using `dotnet run`).
- Chat state: `%LOCALAPPDATA%\Remoter\state.json`.
- Logs: `%LOCALAPPDATA%\Remoter\logs\remoter-YYYY-MM-DD.log` (kept for 14 days).
- Screenshots and temporary diffs: `%TEMP%`, deleted immediately after sending.

## Build

```powershell
dotnet build
dotnet test
dotnet publish src\Remoter.App -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o publish
```

The resulting `publish\Remoter.exe` (with `appsettings.json` next to it) requires the .NET 10 Desktop Runtime. For a fully self-contained build, replace `--self-contained false` with `true`.

## Troubleshooting

- **"TELEGRAM_BOT_TOKEN is not set"** — the token didn't get into the `.env` next to the exe. Check the path in the tray menu "Open .env".
- **The bot is silent** — check that your ID is in `ALLOWED_USER_IDS`, and look at the log.
- **`Free usage limit reached` / `Insufficient balance`** — the provider's quota or balance is exhausted. Choose another model via `/model` or top up your OpenCode account.
- **The OpenCode service won't start** — run `opencode service status` manually. In PowerShell, use `opencode.cmd` (the `.ps1` script may be blocked by the execution policy).
- **`/screen` returns an error** — the screen is locked; this is expected. Tasks continue to run.
- **HTML wasn't sent** — the bot automatically retries the message as plain text.

## Security

- The bot uses long polling only, with no inbound ports.
- The token and allowlist live only in `.env`, which is excluded from git.
- Unknown users are ignored; with an empty allowlist, the bot only tells them their ID.
- Dangerous actions are confirmed with a button; a timeout denies the request.
- `AUTO_APPROVE=true` disables approvals — enable it only on your own machine and remember that anyone who gets access to your Telegram can run commands.
- In the logs, the token is replaced with `***`.

## License

MIT — see [LICENSE](LICENSE). Use it, fork it, and adapt it to your needs.
