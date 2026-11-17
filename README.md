# PDF Reader

**A calm, fast, dark-mode PDF reader for Windows, with optional AI summaries and questions.**

PDF Reader does a few things well. It opens PDFs, shows them clearly, zooms, prints and gets out of your way. It's a small native Windows app, not a browser tab, with no account, no ads and no telemetry. If you want, you can ask ChatGPT or Claude to summarize the PDF you're reading or answer questions about it, using the plan you already pay for.

![PDF Reader showing a document with an AI summary beside it](docs/screenshots/ask-ai.png)

## Features

### Reading
- **A clean, dark interface** that keeps attention on the page.
- **Opens PDFs the way you expect:** double-click a file, drag one onto the window, or press **Ctrl+O**.
- **Page navigation:** the arrow keys, Page Up and Page Down, Home and End, or the page bar on the right. The mouse wheel turns the page when you scroll past the top or bottom.
- **Always know where you are:** a "2 of 4" page count sits on the page and in the toolbar.
- **Zoom:** fits the page to the window by default, with zoom from 10% to 800% using **Ctrl+wheel** or **Ctrl+Plus** and **Ctrl+Minus**.
- **Recent files** on the home screen, with page thumbnails.
- **Printing** through the standard Windows print dialog, with page ranges.

### Select, copy, find and follow links
- **Select text** by dragging across it, as in a web page. Double-click selects a word, and triple-click selects a line. Press **Ctrl+C** or right-click to copy, or **Ctrl+A** to select the whole page.
- **Find** with **Ctrl+F** or the search button. It highlights every match, shows "3 of 12", and jumps between matches and pages with **Enter** and **Shift+Enter**. Searches ignore case and work across line breaks.
- Works with PDFs that contain real text. Scanned pages without a text layer can't be selected.
- **Links work:** click a link to open it in your browser or email app, or to jump to another page of the PDF. Web and email addresses written as plain text are clickable too, and hovering shows where a link goes. For safety, only web and email links open; links that try to run programs or open files are ignored.
- **Tracking numbers are clickable:** UPS, USPS, FedEx and Amazon numbers in a PDF open the carrier's tracking page in your browser. When Ask AI mentions one, a **Track** button appears under the answer. Numbers are checked against each carrier's check digit, so order and phone numbers aren't mistaken for them. PDF Reader doesn't look anything up itself.

![Find highlighting a match, with the match count](docs/screenshots/find.png)

![The reader on page 2 of 4, with the page bar and page count](docs/screenshots/reader.png)

### Ask AI (optional)
- **Summarize** a PDF in one click, or **ask questions** about it in a chat beside the page. Answers cite page numbers.
- **Uses your existing plan, not an API key:**
  - **ChatGPT:** sign in with your ChatGPT account (Plus or Pro) on OpenAI's own page in your browser. PDF Reader never sees your password.
  - **Claude:** uses the [Claude Code](https://code.claude.com/docs/en/setup) app you've already installed and signed in to, with your Claude Pro or Max plan.
- **Private by default:** nothing leaves your PC until you ask a question. Then the PDF and your question go only to the service you picked.

![The home screen with recent files](docs/screenshots/home.png)

![Signing in with a ChatGPT plan](docs/screenshots/sign-in.png)

## Install

1. Download `PDFReaderSetup-<version>.exe` from the [latest release](../../releases/latest).
2. Run it and choose **Install**. It installs for your Windows account only, so it doesn't need admin rights.
3. To make it your default PDF app, choose **Set as default** at the end of the install. You can also go to **Settings > Apps > Default apps**, search for `.pdf`, and pick **PDF Reader**.

Updates: from version 1.4.0, PDF Reader checks GitHub for a newer release when it starts (at most once a day) and shows a bar with **Update** and **Later**. **Update** downloads the installer, checks it against the SHA-256 checksum GitHub publishes for it, installs it and reopens the app with your PDF. To turn the check off, click **Check for updates at start** at the bottom of the home screen. You can also run a newer installer over the top. Your settings and sign-ins stay either way. To uninstall, go to **Settings > Apps > Installed apps**.

**Requirements:** Windows 11, 64-bit (Windows 10 should also work but hasn't been tested). The .NET Framework 4.8 runtime it uses is built into Windows.

> Windows SmartScreen may warn about the installer because it isn't code-signed. Choose **More info**, then **Run anyway**.

## Keyboard shortcuts

| Keys | Action |
| --- | --- |
| Left / Right, Page Up / Page Down | Previous / next page |
| Home / End | First / last page |
| Up / Down | Scroll the page |
| Ctrl+Plus / Ctrl+Minus, Ctrl+wheel | Zoom in / out |
| Ctrl+F, F3 / Shift+F3 | Find, next / previous match |
| Ctrl+C, Ctrl+A | Copy the selection, select the whole page |
| Esc | Close Find or clear the selection |
| Ctrl+O | Open a PDF |
| Ctrl+P | Print |

## Privacy

- PDFs are read on your PC with PDFium. Nothing is uploaded unless you use Ask AI.
- The update check asks GitHub's API for the latest release. It sends nothing about you or your files beyond the request itself (your IP address, and the app name and version). You can turn it off on the home screen.
- Settings, recent files and thumbnails are kept in `%LOCALAPPDATA%\PdfReader`. Sign-in tokens are encrypted to your Windows account.
- Ask AI is stateless: each question sends the PDF and the conversation so far to the service you chose. PDF Reader doesn't keep a copy anywhere else, and ChatGPT requests are sent with storage turned off.

## Build from source

You don't need Visual Studio or an SDK. The build uses the C# compiler that ships with Windows.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

This produces `PdfReader.exe` and the installer at `dist\PDFReaderSetup-<version>.exe`. The version is set in `Setup.cs`. On the first build it downloads a pinned PDFium build and checks its hash.

| File | What it does |
| --- | --- |
| `PdfReader.cs` | The app: window, rendering, zoom, printing, settings and the Ask AI panel |
| `Main.xaml`, `Theme.xaml` | Layout and dark theme |
| `PdfText.cs`, `TextTools.cs` | PDFium: drawing pages, the text layer and links; selecting, copying, Find and clicking links |
| `ChatGptAuth.cs` | Sign in with ChatGPT (browser sign-in with PKCE) |
| `ClaudeCode.cs` | Runs the local Claude Code app for Claude answers |
| `Setup.cs` | Per-user installer and uninstaller |

## Built with

C# and WPF on .NET Framework 4.8. Pages, text, links and printing use [PDFium](https://pdfium.googlesource.com/pdfium/), Chrome's open-source PDF engine, from [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries). It's the only third-party library, and its licenses are in `third_party/pdfium` and installed as `THIRD-PARTY-NOTICES.txt`. Tracking-number formats follow [tracking_number_data](https://github.com/jkeen/tracking_number_data) (MIT).

## License

[MIT](LICENSE). Copyright (c) 2026 Nepal Plummer.
