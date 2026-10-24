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

Google Sheets integration is optional and can be disabled in the configuration file.

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
/setsheet https://docs.google.com/spreadsheets/d/...
```

The bot will use this sheet until another sheet is set or the raid is ended.

---

# Slash Commands

## /startauction

Starts an auction.

Parameters:

| Parameter | Required | Description |
|------------|----------|-------------|
| itemname | Yes | Item being auctioned |
| duration | No | Auction duration in minutes |
| rownumber | No | Row number on the Google Sheet |

Example:

```text
/startauction itemname:Fungi Tunic duration:2
```

---

## /setsheet

Sets the Google Sheet used for the current raid.

Parameters:

| Parameter | Required | Description |
|------------|----------|-------------|
| googlesheeturl | Yes | URL of the Google Sheet |

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

The bot will parse the supplied data and process any loot entries it finds.

This is useful when:

- The bot was not running when loot was awarded.
- The log parser was disabled.
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
    "CredentialFile": "data/service-account.json",
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
