# tgcli

Small command-line tool for reading Telegram chats with TDLib.

## Install

Linux:

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v2.0.0/tgcli-linux-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

Windows PowerShell, as Administrator:

```powershell
New-Item -ItemType Directory -Force "$env:ProgramFiles\tgcli" | Out-Null; Invoke-WebRequest "https://github.com/podkolzzzin/tgcli/releases/download/v2.0.0/tgcli-win-x64.exe" -OutFile "$env:ProgramFiles\tgcli\tgcli.exe"; [Environment]::SetEnvironmentVariable("Path", [Environment]::GetEnvironmentVariable("Path", "Machine") + ";$env:ProgramFiles\tgcli", "Machine")
```

macOS:

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v2.0.0/tgcli-osx-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

Then open a new terminal and run:

```bash
tgcli --help
```

## Agent Skill

Install for Codex:

```bash
mkdir -p ~/.codex/skills/tgcli && curl -fsSL https://raw.githubusercontent.com/podkolzzzin/tgcli/main/.codex/skills/tgcli/SKILL.md -o ~/.codex/skills/tgcli/SKILL.md
```

Install for Claude:

```bash
mkdir -p ~/.claude/skills/tgcli && curl -fsSL https://raw.githubusercontent.com/podkolzzzin/tgcli/main/.claude/skills/tgcli/SKILL.md -o ~/.claude/skills/tgcli/SKILL.md
```

## Login

Create Telegram API credentials at <https://my.telegram.org/apps>, then run:

```bash
tgcli login --api-id 12345 --api-hash your_api_hash
```

You only need to login once. The local Telegram session is stored on your machine.

## Examples

```bash
tgcli chat list
tgcli search "some chat"
tgcli chat messages --chat-id 123456789
tgcli chat export --chat-id 123456789 --format md --output chat.md
tgcli download --chat-id 123456789 --message-id 987654321 --output ./files
```

## Notes

Keep your session directory private. It contains Telegram login data.

This project is unofficial and is not affiliated with Telegram.
