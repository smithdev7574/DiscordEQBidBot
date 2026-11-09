# BidBot

A Discord bot for running silent EverQuest loot auctions and automatically tracking raid splits.

BidBot allows raiders to submit hidden bids on loot through Discord. When an auction ends, the highest bidder wins the item, but only pays **one platinum more than the second-highest bid**.

Example:

- Alice bids 15,000pp
- Bob bids 12,000pp
- Carol bids 10,000pp

Alice wins the item and pays **12,001pp**.

The bot can integrate with Google Sheets to track loot, winners, and winning bids throughout the raid. This makes it easy to calculate platinum splits at the end of the night.

Designed for EverQuest raid guilds that use platinum-based loot auctions and end-of-raid split distributions.

---

# Features

- Silent bidding through Discord
- Winner pays one platinum more than the second-highest bid
- Automatic winner determination
- Item autocomplete when starting auctions
- Google Sheets integration
- EverQuest log parsing
- Manual log imports
- Wishlist support
- Auction history tracking
- Raid split tracking and payout calculations

---

## First Time setup

BidBot reads the EverQuest log on your PC, posts auctions and results in the guild Discord, and writes to the guild’s shared raid sheet. Coordinate with the raid lead so only one BidBot instance handles a raid.

### 1. Create your Discord bot and invite it to the guild

1. Open the [Discord Developer Portal](https://discord.com/developers/applications), select **New Application**, and give it a name that identifies it as your BidBot.
2. Open **Bot**. Under **Privileged Gateway Intents**, enable **Server Members Intent** and **Message Content Intent**, then save. BidBot requests both when it connects.
3. Under **Token**, select **Reset Token** to generate your bot token. Save it somewhere private so you can add it to your local `appsettings.json` in step 5. Keep the token out of Discord messages and Git commits.
4. Open **Installation**. Enable **Guild Install**, choose **Discord Provided Link**, and under **Default Install Settings → Guild Install** add the `bot` and `applications.commands` scopes. Select **View Channel**, **Send Messages**, **Read Message History**, and **Embed Links**. If the guild uses `!bb` commands, grant the bot **Manage Messages** in the channel where those commands are posted so it can delete the command messages it processes.
5. Copy the install link, open it, select **Add to server**, choose the guild Discord, and authorize. A member with **Manage Server** can do this. If you lack that permission, give a guild admin the install link. The bot will appear offline until you run it.
6. **Give BidBot access to the channels in `appsettings.json`.** The bot needs permission to see and post in each configured channel. Ask a guild admin to apply these settings to the channels named under `Settings` as `BidChannelName`, `AwardChannelName`, and `LogChannelName` (when `ShouldLogToChannel` is `true`).

   1. In Discord, hover over the channel and click the gear icon to open **Edit Channel**.
   2. Open **Permissions**. Under **Advanced permissions**, click **+** next to **Roles/Members** and select the installed bot or its bot role.
   3. Set **View Channel**, **Send Messages**, **Read Message History**, and **Embed Links** to **Allow** (the green checkmark). If this channel is used for `!bb` commands, also allow **Manage Messages** so the bot can remove the command messages it processes.
   4. Click **Save Changes**. Repeat for each configured channel, including any private ones.

   The person using `/auction` needs **Manage Server**. In the server’s app integration settings, restrict `/setsheet` and `/endraid` to the raid operators. [Discord’s channel permission guide](https://support.discord.com/hc/en-us/articles/206029707-Setting-Up-Permissions-FAQ)

[Discord’s app creation and installation guide](https://docs.discord.com/developers/quick-start/getting-started)

### 2. Get the bot onto your Windows PC

1. Open [BidBot Releases on GitHub](https://github.com/smithdev7574/DiscordEQBidBot/releases). If GitHub asks for access, contact the project owner.
2. Under **Assets** for the release, download **`BidBot-win-x64.zip`**. The Windows build includes its .NET runtime.
3. In File Explorer, right-click the downloaded ZIP and choose **Extract All**. Open the extracted folder containing `BidBot.exe` and `appsettings.json`. Keep the extracted files together.

### 3. Find the live EverQuest log

1. Log in to the character whose client will see the raid’s loot assignments and enter `/log on` in EverQuest.
2. Find that character’s `eqlog_Character_Server.txt` in the EverQuest `Logs` folder. Confirm it exists and its modified time changes as you play.
3. Copy the full path, including the filename. Keep that EQ client running during the raid. BidBot starts at the end of the file and checks for new entries. It looks for lines containing `was given to`.

### 4. Set up Google Sheets credentials and share the raid folder

1. **Create your Google Cloud project.** Sign in to the [Google Cloud console](https://console.cloud.google.com/). Open the project selector at the top, choose **New Project**, enter a name such as `BidBot`, and click **Create**. Select your new project before continuing.
2. **Enable the Google Sheets API.** In your new project, open **APIs & Services → Library**, search for **Google Sheets API**, select it, and click **Enable**. [Google’s API enablement guide](https://developers.google.com/workspace/guides/enable-apis)
3. **Create a service account.** Open **IAM & Admin → Service Accounts → Create service account**. Name it `bidbot` and click **Create and continue**. You can skip the optional project role and account-access steps, then click **Done**. The **Editor** access BidBot needs is granted on the raid folder in Google Drive in step 6.
4. **Download the service account’s JSON key.** In **Service Accounts**, click the email address of the account you just created. Open **Keys → Add key → Create new key**, select **JSON**, and click **Create**. Your browser downloads a JSON file containing the service account’s private key. BidBot uses this file to sign in to Google. [Google’s service account and key guide](https://developers.google.com/workspace/guides/create-credentials#service-account)
5. **Put the key in BidBot’s Data folder.** In File Explorer, open the extracted release folder containing `BidBot.exe`, then open `Data`. Rename your downloaded JSON file to `service-account.json` and put it there, replacing the existing file if present. In the local `appsettings.json`, set `SheetSettings → CredentialFile` to `Data/service-account.json`. Keep your JSON key private and out of GitHub, Discord, and the shared Drive folder.
6. **Give the service account write access to the guild’s raid folder.** Copy the service account email from the Google Cloud console or the JSON file’s `client_email` field. It ends in `@YOUR-PROJECT-ID.iam.gserviceaccount.com`. Ask the owner of the guild’s raid folder to open it in Google Drive, click **Share**, add that email, and choose **Editor**. They can clear **Notify people** and save the change. Confirm that the raid spreadsheet and any test copy are in the shared folder. For a spreadsheet outside that folder, ask its owner to grant the service account **Editor** access directly. [Google’s folder sharing guide](https://support.google.com/drive/answer/7166529)
7. **Get the spreadsheet details.** Get the raid spreadsheet URL, the exact loot tab name, and the column letters for item, holder, winner, and amount. The loot tab must be first in the workbook. BidBot scans the first tab to find the first empty item cell from row 5 onward, then writes to the configured tab name. Make sure the service account can edit those cells if the sheet has protected ranges.

### 5. Configure and start BidBot

Open the `appsettings.json` next to `BidBot.exe` in Notepad and update the settings below. Keep the existing item-list and other data settings. Replace the example paths, channel names, tab name, and column letters with your actual values. Use doubled backslashes in JSON paths and channel names without the `#` prefix.

| Setting | Example or value |
| --- | --- |
| `Settings → BidChannelName` | `bid-channel` |
| `Settings → AwardChannelName` | `bid-bot-completed-auctions` |
| `Settings → LogChannelName` | `bot-logs` |
| `Settings → ShouldLogToChannel` | `true` if using the log channel, otherwise `false` |
| `Settings → MessagePrefix` | `!bb` |
| `Data → DataFilePath` | `C:\\Users\\YourName\\BidBot` for your own local raid state |
| `Discord → Token` | Replace the placeholder with your own bot token, inside the existing quotation marks. |
| `EQLogParseSettings → ShouldParseLogFile` | `true` |
| `EQLogParseSettings → EQLogFile` | `C:\\EverQuest\\Logs\\eqlog_Character_Server.txt` |
| `EQLogParseSettings → LogParseInterval` | `10` seconds |
| `SheetSettings → CredentialFile` | `Data/service-account.json` |
| `SheetSettings → SheetName` | `Raid Loot!` |
| `SheetSettings → ItemNameColumn` / `HeldByColumn` | `A` / `B` |
| `SheetSettings → WinnerColumn` / `AmountColumn` | `E` / `F` |

Save `appsettings.json` on your PC and keep the edited file out of commits and shared copies. BidBot reads the saved token each time it starts.

Double-click `BidBot.exe` in the extracted folder. Leave the bot window open during the raid.

### 6. Test before the raid

Use a test copy of the guild sheet and Discord channels set aside for testing. Check that your bot comes online and its slash commands appear under its app name.

1. Run `/setsheet sheeturl:` and paste the test spreadsheet URL. The confirmation means the bot accepted the URL. The next step checks that it can write to the sheet.
2. Generate a new `was given to` loot line in the monitored log. Check that the item and holder appear on the expected row. For a test with an existing log, attach a `.txt` file containing only the relevant loot lines to a `!bb manuallog` message while log parsing is enabled.
3. Run `/auction` with `itemname`, `duration` (for example, `1`), and `rownumber` set to the actual row in the test sheet. Have a tester place a bid with the auction button. Keep DMs from server members enabled for bid and winner notifications. Confirm the award posts in Discord and the winner and amount appear in the right cells.
4. Run `/endraid` when the test is complete. If it replies with both an end timestamp and `No active raid`, the end timestamp confirms it ended the raid.

**First-run Google access error.** The first time BidBot tries to use the sheet, Google may return an error with a link asking you to enable access. Open the link from the bot window in your browser and sign in with the Google account that manages the project you created. If the page says the **Google Sheets API** is disabled, confirm it shows your project, then click **Enable**. Wait a few minutes and repeat the test with a new loot line or re-import the missed line. If the API is already enabled and Google reports that the service account lacks permission, have the guild folder owner check that the key’s `client_email` has **Editor** access. [Google’s API enablement guide](https://developers.google.com/workspace/guides/enable-apis)

### Raid-night checklist

1. Confirm who is running the bot. Start EverQuest logging and BidBot before loot is handed out.
2. Run `/setsheet sheeturl:` with the live raid spreadsheet. This starts the bot’s raid session. Set the sheet at the start of each raid since writes expire 24 hours after it was set.
3. Check that new loot appears in the sheet. Start each auction with `/auction` and the matching `rownumber` so the result goes back to the correct row. Keep auctioned rows in place until their results are written.
4. Let all auctions finish and verify the results against the sheet. Run `/endraid`, then close the bot window. Keep the bot running until auctions finish because open bids are held in memory.

| If you see… | Check… |
| --- | --- |
| Bot stays offline or Discord rejects an intent | Confirm `Discord → Token` in your local `appsettings.json` contains the token for the app you installed and that both required intents are enabled. |
| Commands are missing or posts fail | Confirm the app was installed to the server with both scopes. Check the console, the bot’s channel permissions, and the configured channel names. |
| Loot fails to appear | Confirm the file is the active character’s log, the loot line was written after the bot started, and `/setsheet` was run before the loot was assigned. |
| Sheet writes fail | Check the service account’s Editor access, API enablement, key path, first tab and configured tab name, and auction row number. |

See the [RaidSplitDiscordBot setup](https://github.com/smithdev7574/RaidSplitDiscordBot#setup). for the companion bot.

---

# Typical Raid Workflow

1. Start the bot.
2. Run `/setsheet` and provide the Google Sheet URL for the current raid.
3. Loot is assigned to an officer in-game.
4. The EverQuest log parser detects loot messages containing:

```text
was given to
```

5. Detected items are added to the Google Sheet.
6. Auctions are started from either Discord slash commands or text commands.
7. Raiders submit private bids.
8. When the auction ends:
   - The highest bidder wins.
   - The winner pays one platinum more than the second-highest bid.
9. Winner and winning amount are written back to the Google Sheet.
10. At the end of the raid, run `/endraid`.

The resulting sheet contains all auction results needed to calculate and distribute the raid split.

---

# Google Sheets Integration

BidBot uses Google Sheets to track loot and auction results. Follow the setup above to configure the service account and the shared spreadsheet.

The bot is designed around a single active Google Sheet and is **not recommended for use across multiple Discord guilds from a single bot instance**.

Because the sheet integration is tied to a single spreadsheet at a time, running separate bot instances is recommended if multiple guilds need to use the bot concurrently.

## Starting a Raid

At the beginning of the raid, run:

```text
/setsheet
```

and provide the URL of the Google Sheet that will be used for tracking loot and auction results.

Example:

```text
/setsheet sheeturl:https://docs.google.com/spreadsheets/d/...
```

The bot will use this sheet until another sheet is set or the raid is ended.

---

# Slash Commands

## /auction

Starts an auction.

Parameters:

| Parameter | Required | Description |
|------------|----------|-------------|
| itemname | Yes | Item being auctioned |
| duration | No | Auction duration in minutes |
| rownumber | No | Row number on the Google Sheet |

Example:

```text
/auction itemname:Fungi Tunic duration:2
```

---

## /setsheet

Sets the Google Sheet used for the current raid.

Parameters:

| Parameter | Required | Description |
|------------|----------|-------------|
| sheeturl | Yes | URL of the Google Sheet |

---

## /endraid

Ends the current raid session.

Use this command when the raid is complete so the bot stops processing raid activity.

---

## /wishlist

Adds or removes an item from your wishlist.

Parameters:

| Parameter | Required | Description |
|------------|----------|-------------|
| itemname | Yes | Item to add or remove |

---

# Text Commands

In addition to slash commands, BidBot supports traditional text commands.

These commands exist primarily to support Google Sheets integration and workflows where slash commands cannot easily be used.

The default command prefix is:

```text
!bb
```

---

## Start Auction

Starts an auction using a text command.

```text
!bb auction <duration> <rowNumber> <itemName>
```

Example:

```text
!bb auction 2 35 Cloak of Flames
```

Arguments:

| Argument | Description |
|-----------|-------------|
| duration | Auction duration in minutes |
| rowNumber | Row number on the Google Sheet |
| itemName | Item being auctioned |

This command is commonly used by Google Sheets integrations because Discord slash commands cannot be triggered directly from spreadsheets.

The row number is used to link the auction result back to the correct spreadsheet row.

---

## Manual Log Import

If the EverQuest log parser misses loot, log data can be supplied manually using:

```text
!bb manuallog
```

The command accepts either:

- An attached EQ log (`.txt`) file.
- Pasted EverQuest log text in the same message.

Keep live log parsing enabled while importing so the bot processes the supplied entries.

This is useful when:

- The bot was not running when loot was awarded.
- The parser missed an item.
- Historical logs need to be imported.

The parser looks for EverQuest loot messages containing:

```text
was given to
```

Occasionally the parser will identify items that should not be auctioned. If that happens, simply delete the unwanted row from the spreadsheet.

---

# EverQuest Log Parsing

The bot can monitor an EverQuest log file and automatically detect loot being assigned to officers.

Configuration:

| Setting | Description |
|----------|-------------|
| ShouldParseLogFile | Enables or disables log parsing |
| EQLogFile | Full path to the EverQuest log file |
| LogParseInterval | Time between scans in seconds |

Example:

```json
"EQLogParseSettings": {
  "ShouldParseLogFile": true,
  "EQLogFile": "C:\\EverQuest\\Logs\\eqlog_Character_server.txt",
  "LogParseInterval": 10
}
```
## Item Data

The project includes a text file containing EverQuest item references and corresponding Lucy URLs used for item lookup and autocomplete.

When an item is auctioned for the first time, the bot retrieves the item's Lucy page, extracts the relevant item information, and caches it locally for future use.

Lucy / Allakhazam:
https://lucy.allakhazam.com/
---

## Item Database and Lucy Integration

The bot includes a pre-generated item database derived from Lucy (Allakhazam) item data.

This database serves as the master item list used for:

- Item autocomplete
- Auction item validation
- Lucy item lookups

Each item entry contains a Lucy item URL.

When an item is auctioned for the first time:

1. The bot retrieves the item's Lucy page.
2. The relevant HTML is extracted.
3. The item data is cached locally.
4. Future auctions use the cached data rather than requesting the page again.

This allows auction messages to include item statistics and details without repeatedly querying Lucy.

The cache is stored locally and will grow over time as new items are auctioned.


# Configuration

The bot is configured using `appsettings.json`.

Example:

```json
{
  "Settings": {
    "BidChannelName": "bid-channel",
    "LogChannelName": "bot-logs",
    "AwardChannelName": "bid-bot-completed-auctions",
    "ShouldLogToChannel": true,
    "MessagePrefix": "!bb"
  },
  "Data": {
    "ItemListPath": "Data/itemlist.txt",
    "ItemHtmlPath": "Data/ItemHtml.json",
    "DataFilePath": "Data",
    "AuctionHistoryPath": "Data/AuctionHistory.json"
  },
  "Discord": {
    "Token": "YourDiscordTokenHere"
  },
  "EQLogParseSettings": {
    "ShouldParseLogFile": true,
    "EQLogFile": "FullPathIncludingExtensionToLogFileHere",
    "LogParseInterval": 10
  },
  "SheetSettings": {
    "CredentialFile": "Data/service-account.json",
    "SheetName": "Raid Loot!",
    "WinnerColumn": "E",
    "AmountColumn": "F",
    "ItemNameColumn": "A",
    "HeldByColumn": "B"
  }
}
```

---

# Settings

## Discord Settings

| Setting | Description |
|----------|-------------|
| Token | Discord bot token |

---

## General Settings

| Setting | Description |
|----------|-------------|
| BidChannelName | Channel used for active auctions |
| LogChannelName | Channel used for bot logs |
| AwardChannelName | Channel used to announce completed auctions |
| ShouldLogToChannel | Enables Discord log messages |
| MessagePrefix | Prefix for text commands |

---

## Data Settings

| Setting | Description |
|----------|-------------|
| ItemListPath | Path to item list data |
| ItemHtmlPath | Path to cached item HTML data |
| DataFilePath | Root data directory |
| AuctionHistoryPath | Auction history storage file |

---

## Google Sheet Settings

| Setting | Description |
|----------|-------------|
| CredentialFile | Service account JSON credentials |
| SheetName | Worksheet name |
| WinnerColumn | Column used for auction winners |
| AmountColumn | Column used for winning bids |
| ItemNameColumn | Column containing item names |
| HeldByColumn | Column containing the officer holding the item |



# License

This project is licensed under the MIT License.

You are free to use, modify, and distribute the software. It is provided "as is" without warranty of any kind.

---

# Disclaimer

This is a hobby project created for EverQuest raid loot management.

Always verify loot assignments, auction results, and split calculations before distributing platinum.

## Attribution

The item reference list included with this project was derived from data published by Lucy (Allakhazam). Please see Lucy's website for current terms regarding use and attribution of their data.

Lucy / Allakhazam:
https://lucy.allakhazam.com/
