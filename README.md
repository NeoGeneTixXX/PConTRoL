# PConTRoL – Produktionsreihenfolge für die Nesting-Maschine

Mit PConTRoL legst du fest, in welcher Reihenfolge die HOLZ-HER-Maschine die Nest-Aufträge abarbeitet.

- **Links: Auftragsvorrat.** Der Ordner `_NestResult`. Diese Aufträge sieht der Bediener an der Maschine noch nicht.
- **Rechts: An der Maschine.** Der Ordner `_Input`, genau in der Reihenfolge, in der die Maschine abarbeitet.

Die Maschine sortiert `_Input` nach dem Änderungsdatum der Dateien, die **älteste zuerst** (am 22.10.2026 an der Maschine getestet).
PConTRoL setzt die Änderungsdaten so, dass die Maschine genau die angezeigte Reihenfolge übernimmt.

---

## Installation

1. Die Datei `publish\PConTRoL.exe` auf den Rechner kopieren, z. B. nach `C:\Programme\PConTRoL\`.
   Es ist eine einzelne EXE. .NET muss nicht installiert sein.
2. Die EXE starten. Beim ersten Start öffnen sich die **Einstellungen**: Dort den **Hauptordner** wählen, also den Ordner,
   in dem `_Input` und `_NestResult` liegen.
3. Optional „Mit Windows starten“ anhaken. Das Programm startet dann beim Anmelden automatisch im Tray.

PConTRoL kann auf mehreren Rechnern laufen:

- **Pro Benutzer** gespeichert werden Pfad, Farben, Spalten und eingeklappte Gruppen, unter `%APPDATA%\PConTRoL\settings.json`.
- **Gemeinsam für alle Rechner** gelten die Reihenfolge im Vorrat, die Gruppen und das Log, unter `<Hauptordner>\_PConTRoL\`.

## Bedienung

| Was | Wie |
|---|---|
| Auftrag an die Maschine | Von links nach rechts ziehen, oder markieren und **➜ ans Ende** bzw. **⇥ als Nächstes** klicken |
| Auftrag zurück in den Vorrat | Von rechts nach links ziehen oder **⬅ zurück** klicken. Er kommt wieder in seine Job-Gruppe |
| Reihenfolge ändern | Innerhalb einer Liste ziehen, oder die Buttons **Ganz oben / Hoch / Runter / Ganz unten** benutzen |
| Mehrere markieren | Strg + Klick oder Umschalt + Klick, dann alle zusammen ziehen |
| Gruppe bilden | Aufträge markieren, dann **＋ Gruppe** oder Rechtsklick → *Gruppe aus Auswahl bilden* |
| Leere Gruppe | **＋ Gruppe** ohne Markierung, dann Aufträge hineinziehen (auf die Mitte der Gruppenzeile) |
| Gruppe verschieben | Die Gruppenzeile ziehen. Alle Platten der Gruppe wandern mit, auch an die Maschine |
| Auf-/Zuklappen | Auf ▶/▼ klicken oder die Gruppenzeile doppelklicken |
| Spalten | Rechtsklick auf die Spaltenköpfe: ein- und ausblenden. Ziehen: Reihenfolge, Rand ziehen: Breite |
| Vollbild | **F11** oder Button *Vollbild* |
| Sofort neu einlesen | **F5**. Sonst liest das Programm alle 3 Sekunden automatisch neu ein |

**Wohin ein gezogener Auftrag fällt:**

- Auf die obere bzw. untere Hälfte einer Zeile: davor bzw. dahinter. Liegt die Zeile in einer Gruppe, kommt der Auftrag in diese Gruppe.
- Auf die Mitte einer Gruppenzeile: in die Gruppe.
- Auf den oberen oder unteren Rand einer Gruppenzeile: vor bzw. hinter die Gruppe.
- Auf die freie Fläche unter der Liste: ans Ende.

Eine gelbe Linie bzw. ein gelber Rahmen zeigt vor dem Loslassen, wohin der Auftrag fällt.

**Der laufende Auftrag** (grün, ⚙) ist **immer der oberste** an der Maschine.
Er wird nie verschoben, und sein Datum wird nie geändert. Alles, was man „ganz nach oben“ schiebt, landet direkt dahinter.

Unten im Fenster stehen zum markierten Auftrag das Vorschaubild (.jpg aus BetterNest), die Daten und die Teileliste aus der .lbl-Datei.

### Tray und Pop-up

- Fenster schließen oder minimieren: PConTRoL läuft im Infobereich weiter.
- **Klick** aufs Tray-Symbol: Pop-up unten rechts mit dem laufenden Auftrag und den nächsten 10.
- **Doppelklick** aufs Tray-Symbol: Hauptfenster.
- **Rechtsklick** aufs Tray-Symbol: Menü, darin **Beenden**.
- Farben, Deckkraft und „Immer im Vordergrund“ stellst du unter *Einstellungen → Pop-up* ein, mit Live-Vorschau.

### Testmodus

Unter *Einstellungen → Testmodus* einen Testordner wählen und den Testmodus einschalten.
Die Schaltfläche *Testordner mit einer Kopie des Hauptordners füllen* kopiert `_Input` und `_NestResult` dorthin.
Im Hauptordner wird dabei nichts verändert. Solange der Testmodus aktiv ist, zeigt das Fenster ein orangefarbenes **TESTMODUS**-Schild.

## Sicherheit

- Nest-Dateien werden **nie gelöscht, nie überschrieben und nie inhaltlich verändert**.
  Erlaubt sind nur zwei Dinge: Verschieben zwischen `_NestResult\<Job>\` und `_Input`, und das Setzen des Änderungsdatums.
- Verschoben werden `.hop` und `.lbl`.
  - `.jpg` (Vorschau) und `.ups` bleiben im Job-Ordner.
  - Ein Vorschaubild, das z. B. HOLZ-HER Production Control nach `_Input` mitgenommen hat, wandert beim Zurückholen wieder mit in den Job-Ordner.
- Reihenfolge beim Verschieben zur Maschine:
  1. Zuerst wird das Datum gesetzt. So erscheint der Auftrag in `_Input` sofort an der richtigen Stelle.
  2. Dann wird zuerst die `.lbl` verschoben, danach die `.hop`.
- **Gesperrte Dateien** (die Maschine liest gerade):
  1. PConTRoL versucht es automatisch mehrmals.
  2. Klappt es nicht, macht es alle Schritte dieser Aktion rückgängig und fragt „Erneut versuchen?“.
  3. Es bleibt kein halber Zustand zurück.
- Ziel existiert schon: Die Aktion wird abgebrochen, es wird nichts überschrieben.
- **Log:** Jede Aktion landet in `<Hauptordner>\_PConTRoL\aktionen_JJJJ-MM.log`, mit Zeit, Rechner und Benutzer, Aktion, Datei sowie altem und neuem Datum.
  Ist der Ordner nicht beschreibbar, schreibt PConTRoL ersatzweise nach `%APPDATA%\PConTRoL\`.

## Für Entwickler

```
src\PConTRoL.Core    Logik: Einlesen, Reihenfolge→Datum, Gruppen, sicheres Verschieben, Log
src\PConTRoL.App     WPF-Oberfläche, Tray, Pop-up, Einstellungen
tests\PConTRoL.Tests xUnit-Tests (Datumslogik, Gruppen, Verschieben inkl. gesperrter Dateien)
```

- **Sortierrichtung der Maschine:** Konstante `MachineConfig.SortOrder` in `src\PConTRoL.Core\MachineConfig.cs`. Kein Einstellungspunkt.
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

### Annahmen aus den Beispieldateien

- Gelesen werden die `.lbl` (INI-Format, ANSI oder UTF-16) und der Kopf der `.hop` bis `START`. Der NC-Code wird ignoriert.
- Spalte *Prod.-Nr.*: `_hhdata_ProductionID` aus der .hop. Fehlt sie, kommt `FreeText7` der Teile zum Zug. Die Bedeutung von `FreeText7` ist geraten.
- `WorkTime` ist in den Beispielen immer 0, deshalb gibt es keine Laufzeit-Spalte.
- Der Nest-Job (Unterordner) wird aus `HopFilename` in der .lbl bestimmt. Fallback: Name der .nst, sonst der Dateiname ohne `_01`.
