# Pro Pinball – Big Race USA: LAN-Patch

Head-To-Head im Heimnetz unter Windows 10/11 – ohne DirectPlay-Gefrickel.

Getestet mit der GOG-Version. Das Spiel selbst ist nicht enthalten.

## Download

**BigRace-LAN-Patcher.exe** unter [Releases](../../releases). Mehr braucht man nicht – alles Weitere steckt in der EXE.

## Installieren (auf jedem PC)

- Spiel schließen
- `BigRace-LAN-Patcher.exe` starten, Administratorabfrage bestätigen
- Spielordner prüfen, **Patch anwenden**
- Am Ende kommt „Fertig.“ – sonst steht da, was fehlt

Installiert wird:

- Fenstermodus ([cnc-ddraw](https://github.com/FunkyFr3sh/cnc-ddraw))
- `dinput.dll` – zeigt Mitspieler, Lobby und nächsten Schritt im Fenstertitel
- LAN-Launcher + Verknüpfung „Big Race USA LAN“ auf dem Desktop
- Firewall-Freigaben fürs Heimnetz (Spiel, UDP 47700, TCP 2914)

**Patch entfernen** im selben Programm macht alles wieder rückgängig.

## Spielen

- „Big Race USA LAN“ starten, Fahrernamen eintragen
- Warten, bis die anderen im Fahrerfeld stehen
- **START**
- Im Spiel erst den Tisch laden, dann Esc › Head-To-Head › Friendly (Lobby)
  (Esc im Hauptmenü beendet das Spiel)
- In der Lobby den Gegner anklicken – das Match startet

Geht auch mit mehr Leuten, alle landen in derselben Lobby. Easy/Normal/Hard in der Liste sind Computergegner.

## Wenn es hakt

- **Fahrerfeld bleibt leer:** im selben Netz? VPN (Tailscale, Hamachi, ZeroTier) trennen. Patcher auf allen PCs gelaufen?
- **Lobby-PC wurde geschlossen:** in der Lobby Cancel, Lobby neu öffnen
- **WLAN:** Gastnetz/Client-Isolation am Router aus
- **Andere Firewall** (Norton, Kaspersky, Bitdefender …): `BigRaceUSA.exe` und `BigRace-LAN-Launcher.exe` dort erlauben
- **Protokoll:** `%TEMP%\BigRace-Lobby-Protokoll.txt`

## Wie es funktioniert

Der eingebaute „Friendly (DirectPlay)“-Modus läuft auf aktuellem Windows nicht zuverlässig. Das Spiel hat aber noch einen zweiten Weg: die längst abgeschaltete **Empire Lobby**. In dem Modus sprechen die Spiele mit einem Lobby-Server und verbinden sich fürs Match direkt per TCP – ganz ohne DirectPlay.

Der Lobby-Server ist im Launcher nachgebaut (`src/Lobby.cs`, Protokoll dort beschrieben). Jeder Launcher hat ihn an Bord; benutzt wird der des Launchers, der zuerst geöffnet wurde. Die Launcher finden sich per UDP-Broadcast und tragen die Lobby-Adresse in `propin.ini` ein (`[empire] Server=`).

Die `dinput.dll` ist ein Proxy für die echte `dinput.dll` (das Spiel importiert daraus nur `DirectInputCreateA`). Sie schreibt die Infos des Launchers in den Fenstertitel und holt Dialoge des Spiels nach vorne. Es wird nichts injiziert und an der Spiel-EXE nichts verändert.

## Selbst bauen

`src\build.cmd` – braucht die Visual C++ Build Tools (für die `dinput.dll`, 32 Bit) und den C#-Compiler aus dem .NET Framework 4, der bei Windows dabei ist.

## Lizenz

Eigener Code: MIT, siehe [LICENSE](LICENSE).
cnc-ddraw: MIT, © FunkyFr3sh, siehe `cnc-ddraw/LICENSE.txt`.

Pro Pinball – Big Race USA ist ein Spiel von Cunning Developments / Empire Interactive. Dieses Projekt hat nichts mit den Rechteinhabern zu tun.
