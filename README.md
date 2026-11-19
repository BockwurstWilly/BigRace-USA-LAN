# Pro Pinball – Big Race USA: LAN Patch

Head-to-head over your home network on Windows 10/11 – without DirectPlay trouble.

Tested with the GOG release. The game itself is not included. The patcher and the launcher speak German; the steps below explain every button.

*LAN-Patch für Head-To-Head im Heimnetz – deutsche Kurzfassung unten.*

## Download

**BigRace-LAN-Patcher.exe** from [Releases](../../releases). That's all you need – everything else is inside the EXE.

## Install (on every PC)

- Close the game
- Run `BigRace-LAN-Patcher.exe` and confirm the administrator prompt
- Check the game folder, click **Patch anwenden** (apply patch)
- At the end it says "Fertig." (done) – otherwise it tells you what is missing

What gets installed:

- Window mode ([cnc-ddraw](https://github.com/FunkyFr3sh/cnc-ddraw))
- `dinput.dll` – shows other players, the lobby and the next step in the window title
- LAN launcher + desktop shortcut "Big Race USA LAN"
- Firewall rules for the home network (game, UDP 47700, TCP 2914)

**Patch entfernen** (remove patch) in the same program undoes everything.

## Play

- Start "Big Race USA LAN", enter a driver name
- Wait until the others appear in the field
- **START**
- In the game load the table first, then Esc › Head-To-Head › Friendly (Lobby)
  (Esc in the main menu quits the game)
- Click your opponent in the lobby – the match starts

Works with more players too; everybody ends up in the same lobby. Easy/Normal/Hard in the list are computer opponents.

## Troubleshooting

- **The field stays empty:** same network? Disconnect VPNs (Tailscale, Hamachi, ZeroTier). Did the patcher run on every PC?
- **The lobby PC was closed:** Cancel in the lobby, open the lobby again
- **Wi-Fi:** switch off guest network / client isolation on the router
- **Other firewall** (Norton, Kaspersky, Bitdefender …): allow `BigRaceUSA.exe` and `BigRace-LAN-Launcher.exe` there
- **Log:** `%TEMP%\BigRace-Lobby-Protokoll.txt`

## How it works

The built-in "Friendly (DirectPlay)" mode doesn't work reliably on current Windows. But the game has a second way: the long-gone **Empire Lobby**. In that mode the games talk to a lobby server and connect directly over TCP for the match – no DirectPlay at all.

The lobby server is rebuilt inside the launcher (`src/Lobby.cs`, protocol described there). Every launcher carries one; the one of the launcher opened first is used. The launchers find each other by UDP broadcast and write the lobby address into `propin.ini` (`[empire] Server=`).

The `dinput.dll` is a proxy for the real `dinput.dll` (the game only imports `DirectInputCreateA` from it). It writes the launcher's information into the window title and brings the game's dialogs to the front. Nothing is injected and the game's EXE is not changed.

## Building

`src\build.cmd` – needs the Visual C++ Build Tools (for the 32-bit `dinput.dll`) and the C# compiler of the .NET Framework 4, which is part of Windows.

## License

© 2026 [BockwurstWilly](https://github.com/BockwurstWilly) – own code and docs are licensed under the
**[PolyForm Noncommercial License 1.0.0](LICENSE)**: you may use, share and change it for any non-commercial purpose,
as long as the `Required Notice` line (credit to BockwurstWilly with the link) stays with every copy.
Commercial use only by arrangement – contact me via GitHub. (Until 2026-11-19 the license was MIT.)

cnc-ddraw: MIT, © FunkyFr3sh, see `cnc-ddraw/LICENSE.txt`.

Pro Pinball – Big Race USA is a game by Cunning Developments / Empire Interactive. This project is not affiliated with the rights holders.

---

## Deutsch

Head-To-Head im Heimnetz unter Windows 10/11 ohne DirectPlay: `BigRace-LAN-Patcher.exe` auf jedem PC starten, **Patch anwenden**, dann über „Big Race USA LAN“ spielen – im Spiel Tisch laden, Esc › Head-To-Head › Friendly (Lobby), Gegner anklicken. **Patch entfernen** macht alles rückgängig. Getestet mit der GOG-Version, das Spiel ist nicht enthalten.

Lizenz: PolyForm Noncommercial 1.0.0 – frei nutzen, weitergeben und verändern, aber nicht kommerziell; der Hinweis auf BockwurstWilly muss bleiben. Kommerzielle Nutzung nur nach Absprache.
