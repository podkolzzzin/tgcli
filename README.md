# tgcli

Small command-line tool for reading Telegram chats with TDLib.

## Install

Download the binary for your OS from GitHub Releases.

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
