# PConTRoL – Produktionsreihenfolge für die Nesting-Maschine

Mit PConTRoL legst du fest, in welcher Reihenfolge die HOLZ-HER-Maschine die Nest-Aufträge abarbeitet.

- **Links: Auftragsvorrat.** Der Ordner `_NestResult`. Diese Aufträge sieht der Bediener an der Maschine noch nicht.
- **Rechts: An der Maschine.** Der Ordner `_Input`, genau in der Reihenfolge, in der die Maschine abarbeitet.

Der **JobManager** an der Maschine sortiert `_Input` beim Programmstart **zuerst nach der Production ID** (`_hhdata_ProductionID` in der .hop, aufsteigend)
und danach nach dem Änderungsdatum, **älteste zuerst** (am 22.10.2026 an der Maschine getestet).
PConTRoL ersetzt HOLZ-HER Production Control: Es gibt jedem Auftrag beim Schicken eine fortlaufende, höhere Production ID und ein späteres Datum.
So stimmt die Reihenfolge auch nach einem Neustart des JobManagers. Production Control wird nicht mehr benutzt und darf nicht parallel laufen:
Findet PConTRoL beim Start `ProductionControl.exe` auf demselben Rechner, fragt es, ob Production Control beendet werden soll.
Bei „Nein“ (oder wenn das Beenden nicht klappt) startet PConTRoL nicht. Der JobManager wird nie angefasst.

**An der Maschine ist die Reihenfolge fest:** Ein Auftrag, der zur Maschine geschickt wird, reiht sich immer ganz unten ein.
Die Maschine arbeitet die Aufträge also in der Reihenfolge ab, in der sie hinübergeschickt wurden. Umsortiert wird nur im Vorrat.

---

## Installation (einmalig auf dem Nesting-Rechner)

1. Auf <https://github.com/NeoGeneTixXX/PConTRoL/releases/latest> die Datei **`PConTRoL-win-Setup.exe`** herunterladen
   und starten. Es sind keine Administratorrechte nötig, und .NET muss nicht installiert sein.
   - PConTRoL wird für den angemeldeten Windows-Benutzer nach `%LOCALAPPDATA%\PConTRoL` installiert.
   - Es legt eine Verknüpfung auf dem Desktop und im Startmenü an.
   - Die Setup-Datei ist nicht signiert. Zeigt Windows „Der Computer wurde durch Windows geschützt“, auf *Weitere Informationen → Trotzdem ausführen* klicken.
2. Beim ersten Start öffnen sich die **Einstellungen**: Dort den **Hauptordner** wählen, also den Ordner,
   in dem `_Input` und `_NestResult` liegen.
3. „Mit Windows starten“ anhaken. Das Programm startet dann beim Anmelden automatisch im Tray.
4. Wer vorher die alte, lose `PConTRoL.exe` benutzt hat: diese löschen und den alten Autostart-Eintrag durch Schritt 3 ersetzen.
   Die Einstellungen bleiben erhalten, weil sie in `%APPDATA%` liegen.

Deinstallieren geht über *Windows-Einstellungen → Apps*.

### Automatische Updates

- PConTRoL prüft **beim Start und danach alle 4 Stunden**, ob es auf GitHub ein neues Release gibt. Ist eines da, wird es im Hintergrund heruntergeladen.
- Danach erscheinen ein gelber Hinweis im Hauptfenster und eine Meldung am Tray-Symbol: **„Neue Version x.y.z, jetzt aktualisieren?“**, zusammen mit den Änderungsnotizen.
  - **Jetzt aktualisieren**: PConTRoL beendet sich kurz, spielt das Update ein und startet neu.
    Läuft gerade eine Aktion (Verschieben, Datum setzen), wird nichts eingespielt. Dann einfach danach noch einmal klicken.
  - **Später**: Das Update wird beim **nächsten Programmstart** eingespielt.
  - Das Tray-Menü zeigt zusätzlich den Eintrag *Neue Version … installieren*.
- **Von Hand:** *Einstellungen → Programm → Nach Updates suchen*. Dort steht auch die installierte Version.
  Ist ein Update da, erscheint **Jetzt installieren**: Die Einstellungen werden gespeichert, dann läuft das Update wie oben.
- Ein Update wird **nie automatisch während der Arbeit** eingespielt.
- Einstellungen (`%APPDATA%\PConTRoL`), die Reihenfolge und das Log (`<Hauptordner>\_PConTRoL`) liegen außerhalb des Programmordners und überstehen jedes Update.
- Schlägt ein Update fehl, läuft die bisherige Version weiter. Das Ergebnis steht in `%APPDATA%\PConTRoL\update.log`.
  Details des Updaters stehen im Velopack-Log unter `%LOCALAPPDATA%\PConTRoL\`.
- Die installierte Version steht im Hauptfenster oben neben „PConTRoL“.

PConTRoL kann auf mehreren Rechnern laufen:

- **Pro Benutzer** gespeichert werden Pfad, Größe, Farben, Spalten und eingeklappte Gruppen, unter `%APPDATA%\PConTRoL\settings.json`.
- **Gemeinsam für alle Rechner** gelten die Reihenfolge im Vorrat, die Gruppen, das Log und die Statistik, unter `<Hauptordner>\_PConTRoL\`.

## Bedienung

| Was | Wie |
|---|---|
| Auftrag an die Maschine | Von links nach rechts ziehen, oder markieren und **➜ ans Ende** klicken. Er kommt immer ganz unten in die Liste |
| Auftrag zurück in den Vorrat | Von rechts nach links ziehen oder **⬅ zurück** klicken. Er kommt wieder in seine Job-Gruppe |
| Priorität ändern | Nur im Vorrat: ziehen, oder die Buttons **Ganz oben / Hoch / Runter / Ganz unten** benutzen. Gruppenzeile markiert: die ganze Gruppe wandert. Aufträge innerhalb einer Gruppe markiert: es wird innerhalb der Gruppe sortiert |
| Gruppe nach Namen sortieren | Im Vorrat Rechtsklick auf die Gruppe (oder auf Aufträge darin) → *Gruppe nach Name sortieren (A–Z)*. Zahlen zählen numerisch: `_2` kommt vor `_10` |
| Automatik | Button **Automatik AN/AUS** in der Mitte. Ist die Maschine **60 Sekunden leer**, schickt PConTRoL den obersten Eintrag des Vorrats zur Maschine: einen Einzelauftrag oder eine Gruppe komplett |
| Mehrere markieren | Strg + Klick oder Umschalt + Klick, dann alle zusammen ziehen |
| Gruppe bilden | Im Vorrat Aufträge markieren, dann **＋ Gruppe** oder Rechtsklick → *Gruppe aus Auswahl bilden* |
| Leere Gruppe | **＋ Gruppe** ohne Markierung, dann Aufträge hineinziehen (auf die Mitte der Gruppenzeile) |
| Gruppe verschieben | Die Gruppenzeile ziehen. Alle Platten der Gruppe wandern mit, auch an die Maschine |
| Auf-/Zuklappen | Auf ▶/▼ klicken oder die Gruppenzeile doppelklicken |
| Spalten | Rechtsklick auf die Spaltenköpfe: ein- und ausblenden. Ziehen: Reihenfolge (z. B. *Prio* nach vorne), Rand ziehen: Breite. Eine Nummernspalte gibt es nicht: An der Maschine läuft es immer von oben nach unten |
| Spalten der Teileliste | *Einstellungen → Teileliste*: eigene Namen, Reihenfolge (▲/▼) und welche Spalten sichtbar sind. Ziehen am Spaltenkopf unten wird ebenfalls gespeichert |
| Gleiche Teile | In der Teileliste werden Teile, die in **allen angezeigten Spalten** gleich sind, zu einer Zeile zusammengefasst. Die Stückzahl steht in der festen ersten Spalte **ST** |
| Einstellungsfenster | Frei in der Größe veränderbar (Rand oder Greifer unten rechts ziehen); die Größe wird gemerkt. Der Inhalt folgt *Größe → Programmfenster* |
| Vollbild | **F11** oder Button *Vollbild* |
| Sofort neu einlesen | **F5**. Sonst liest das Programm alle 3 Sekunden automatisch neu ein |

**Wohin ein gezogener Auftrag im Vorrat fällt** (an der Maschine kommt er immer ganz unten an):

- Auf die obere bzw. untere Hälfte einer Zeile: davor bzw. dahinter. Liegt die Zeile in einer Gruppe, kommt der Auftrag in diese Gruppe.
- Auf die Mitte einer Gruppenzeile: in die Gruppe.
- Auf den oberen oder unteren Rand einer Gruppenzeile: vor bzw. hinter die Gruppe.
- Auf die freie Fläche unter der Liste: ans Ende.

Eine gelbe Linie bzw. ein gelber Rahmen zeigt vor dem Loslassen, wohin der Auftrag fällt.

**Automatik:** Der Schalter gilt für alle Rechner (gespeichert in `<Hauptordner>\_PConTRoL\reihenfolge.json`).
Nachgefüllt wird von dem Rechner, auf dem PConTRoL zuerst die 60 Sekunden voll hat; die Sperrdatei `automatik.lock` verhindert doppeltes Schicken.
Unter dem Schalter steht, wie lange es noch dauert. Schlägt ein Versuch fehl (z. B. Datei gesperrt), steht das dort in Rot, und nach weiteren 60 Sekunden wird es erneut versucht.
Jedes Ein-/Ausschalten und jedes automatische Schicken steht im Log (Aktion `AUTOMATIK`).

**Der laufende Auftrag** (grün, ⚙) ist **immer der oberste** an der Maschine.
Er wird nie verschoben, und sein Datum wird nie geändert.

Unten im Fenster stehen zum markierten Auftrag das Vorschaubild (.jpg aus BetterNest), die Daten und die Teileliste aus der .lbl-Datei.

### Tray und Pop-up

- Fenster schließen oder minimieren: PConTRoL läuft im Infobereich weiter.
- **Klick** aufs Tray-Symbol: Pop-up unten rechts mit dem laufenden Auftrag und den nächsten 10.
- **Doppelklick** aufs Tray-Symbol: Hauptfenster.
- **Rechtsklick** aufs Tray-Symbol: Menü, darin **Beenden**.
- Farben, Deckkraft und „Immer im Vordergrund“ stellst du unter *Einstellungen → Pop-up* ein, mit Live-Vorschau.

### Größe

Unter *Einstellungen → Größe* lassen sich das Programmfenster und das Pop-up getrennt vergrößern oder verkleinern, jeweils von 75 % bis 200 %.
Die Vorschau ist sofort sichtbar, *Abbrechen* stellt den alten Wert wieder her.
Bei großen Werten am besten im Vollbild arbeiten, oder den Detailbereich unten kleiner ziehen.

### Statistik

Der Button **Statistik** zeigt für jedes Material ein Kreisdiagramm mit **Ausnutzung** (grün) und **Verschnitt** (rot), dazu die Zahl der Platten und die Flächen in m².

- **Was gezählt wird:** jede Platte, die die Maschine fertig bearbeitet hat.
  PConTRoL erkennt das daran, dass sie aus `_Input` verschwindet, ohne dass PConTRoL sie zurückgeholt hat.
  Das funktioniert nur, solange PConTRoL läuft. Löscht jemand einen Auftrag an der Maschine von Hand, zählt er ebenfalls als fertig.
- **Ausnutzung** = Summe der Teileflächen (Breite × Länge jedes Teils aus der .lbl) / Plattenfläche. Das entspricht dem Wert *Efficiency* von BetterNest.
  **Verschnitt** = 100 % − Ausnutzung.
- **Durchschnitt je Material** ist flächengewichtet: Summe der genutzten Flächen / Summe der Plattenflächen.
- **Materialien** erscheinen automatisch, sobald eine Platte mit einem neuen Material fertig wird.
  Leerzeichen und Groß-/Kleinschreibung spielen keine Rolle: „Dekor 12mm“ und „ dekor  12MM“ gelten als dasselbe Material.
- **Keine Doppelzählung:** Jede Platte wird über den Inhalt ihrer .lbl erkannt, auch wenn sie mehrfach gesehen wird.
- **Zeitraum:** oben auswählen, z. B. *Dieser Monat* oder *Letztes Jahr*, oder selbst *von/bis* eintragen.
- **Export:** *Übersicht als CSV* (je Material) und *Platten als CSV* (jede Platte einzeln), jeweils für den gewählten Zeitraum, direkt in Excel zu öffnen.
- Gespeichert wird in `<Hauptordner>\_PConTRoL\statistik.jsonl`, eine Zeile pro Platte, gemeinsam für alle Rechner.

## Sicherheit

- Nest-Dateien werden **nie gelöscht und nie überschrieben**. Erlaubt sind nur drei Dinge:
  1. Verschieben zwischen `_NestResult\<Job>\` und `_Input`.
  2. Das Setzen des Änderungsdatums.
  3. Beim Schicken zur Maschine die Zeile `_hhdata_ProductionID := …;` im Kopf der .hop (vor `START`), wie früher Production Control.
     Fehlt sie, wird sie direkt vor `START` eingefügt. Alle anderen Bytes, die Kodierung und das Änderungsdatum bleiben gleich; der NC-Code wird nie angefasst.
     PConTRoL vergibt IDs ab 1000 und immer höher als alles, was an der Maschine liegt (999 heißt im JobManager „Platte vorziehen“).
- Dateien, die schon an der Maschine liegen, werden nie verändert, auch nicht ihr Datum.
- Verschoben werden `.hop` und `.lbl`.
  - `.jpg` (Vorschau) und `.ups` bleiben im Job-Ordner.
  - Ein Vorschaubild, das z. B. HOLZ-HER Production Control nach `_Input` mitgenommen hat, wandert beim Zurückholen wieder mit in den Job-Ordner.
- Reihenfolge beim Verschieben zur Maschine:
  1. Zuerst werden Production ID und Datum gesetzt, noch im Vorrat. So erscheint der Auftrag in `_Input` sofort ganz unten.
  2. Dann wird zuerst die `.lbl` verschoben, danach die `.hop`.
- **Gesperrte Dateien** (die Maschine liest gerade):
  1. PConTRoL versucht es automatisch mehrmals.
  2. Klappt es nicht, macht es alle Schritte dieser Aktion rückgängig und fragt „Erneut versuchen?“.
  3. Es bleibt kein halber Zustand zurück.
- Ziel existiert schon: Die Aktion wird abgebrochen, es wird nichts überschrieben.
- **Log:** Jede Aktion landet in `<Hauptordner>\_PConTRoL\aktionen_JJJJ-MM.log`, mit Zeit, Rechner und Benutzer, Aktion, Datei sowie altem und neuem Datum
  (Production ID: Aktion `PROD-ID` mit alter und neuer ID).
  Ist der Ordner nicht beschreibbar, schreibt PConTRoL ersatzweise nach `%APPDATA%\PConTRoL\`.

## Für Entwickler

```
src\PConTRoL.Core    Logik: Einlesen, Reihenfolge→Datum, Gruppen, sicheres Verschieben, Log, Statistik
src\PConTRoL.App     WPF-Oberfläche, Tray, Pop-up, Einstellungen, Statistikfenster
tests\PConTRoL.Tests xUnit-Tests (Datumslogik, Gruppen, Verschieben inkl. gesperrter Dateien, Statistik)
```

- **Statistik** (`src\PConTRoL.Core\Statistics`), getrennt von der Oberfläche:
  - `FinishedJobTracker` erkennt Platten, die die Maschine fertig bearbeitet hat.
  - `StatisticsStore` speichert sie (JSON Lines, nur anhängen, Dubletten über den SHA-256 der .lbl).
  - `StatisticsService` wertet aus (flächengewichtet je Material, Zeitfilter).
  - `StatisticsCsv` exportiert nach Excel.
- Die Kreisdiagramme zeichnet das kleine eigene Steuerelement `Views\PieChart.cs`.
  Eine Diagramm-Bibliothek (z. B. LiveCharts2 mit SkiaSharp) würde die EXE und jedes Update deutlich größer machen und mehr können, als hier gebraucht wird.

- **Sortierrichtung der Maschine:** Konstante `MachineConfig.SortOrder` in `src\PConTRoL.Core\MachineConfig.cs`. Kein Einstellungspunkt.
- **Production ID:** `HopProductionId` (setzt die Zeile byte-genau), Sortierung wie der JobManager in `FolderScanner.SortForMachine`.
  Die zuletzt vergebene ID steht in `reihenfolge.json` (`LastProductionId`).
- **Datumslogik:** `TimestampPlanner`.
  - Der laufende Auftrag behält sein Datum.
  - Ein bereits richtig sortierter Anfang der Liste bleibt unverändert.
  - Der Rest bekommt Zeitstempel im Abstand von 2 s (mindestens 1 s), in ganzen Sekunden und möglichst nicht in der Zukunft.
- Gebaut wird **nur** per Publish. Alle Zwischenstände landen unter `publish\_build` (siehe `Directory.Build.props`):

```
dotnet test tests\PConTRoL.Tests -c Release
dotnet publish src\PConTRoL.App -c Release -o publish
```

- Das App-Symbol erzeugt `tools\make-icon.ps1`.
- **Testdaten:** Die Dateien in `tests\PConTRoL.Tests\TestData` sind erfundene Beispiele im Aufbau echter Nest-Dateien.
  **Keine echten Auftragsdaten, Pfade oder Rechnernamen einchecken.** Das Repo ist öffentlich.
  Die `.gitignore` blockiert `.hop`, `.lbl`, `.nst`, Bilder, Logs und `settings.json` außerhalb von `TestData`.

### Neue Version veröffentlichen

1. Code ändern, dann lokal testen: `dotnet test tests\PConTRoL.Tests -c Release`.
2. In `src\PConTRoL.App\PConTRoL.App.csproj` die `<Version>` erhöhen (SemVer):
   - **Patch** (1.2.0 → 1.2.1) für Fehlerbehebungen
   - **Minor** (1.2.0 → 1.3.0) für neue Funktionen
   - **Major** (1.x → 2.0.0) für grundlegende Änderungen
3. Committen und pushen.
4. Ein Tag mit derselben Versionsnummer setzen und pushen. Die Nachricht des Tags ist der Änderungstext, den die App im Update-Hinweis anzeigt:

```
git tag -a v1.3.0 -m "Neue Spalte Materialstärke; Pop-up merkt sich die Position"
git push origin v1.3.0
```

5. GitHub Actions (`.github/workflows/release.yml`) übernimmt den Rest:
   - prüft, dass Tag und `<Version>` übereinstimmen
   - führt die Tests aus
   - baut die EXE für Windows x64
   - packt sie mit Velopack
   - veröffentlicht sie unter *Releases*

   Den Fortschritt siehst du im Reiter *Actions*. Spätestens 4 Stunden danach, oder beim nächsten Start, meldet sich das Update auf dem Nesting-Rechner.

Hat sich ein Fehler eingeschlichen, nicht das Release löschen, sondern einfach eine höhere Version mit der Korrektur veröffentlichen.
Velopack aktualisiert nur auf höhere Versionsnummern.

### Annahmen aus den Beispieldateien

- Gelesen werden die `.lbl` (INI-Format, ANSI oder UTF-16) und der Kopf der `.hop` bis `START`. Der NC-Code wird ignoriert.
- Spalte *Prod.-Nr.*: `_hhdata_ProductionID` aus der .hop. Fehlt sie, kommt `FreeText7` der Teile zum Zug. Die Bedeutung von `FreeText7` ist geraten.
- `WorkTime` ist in den Beispielen immer 0, deshalb gibt es keine Laufzeit-Spalte.
- Der Nest-Job (Unterordner) wird aus `HopFilename` in der .lbl bestimmt. Fallback: Name der .nst, sonst der Dateiname ohne `_01`.
