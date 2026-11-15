# Imagepixel Backup

Desktop-Programm von Imagepixel (www.imagepixel.de) für Windows 10 und 11 und macOS: Notebooks und PCs mit restic verschlüsselt sichern und Dateien selbst wiederherstellen. Beschreibung der Funktionen: `README.md`.

**Dieses Repository ist öffentlich.** Hier keine Zugangsdaten, Kundendaten, internen Adressen oder Einzelheiten zu den nicht öffentlichen Teilen von Imagepixel ablegen.

## Zusammenhang mit den anderen Teilen

- Wie das Programm mit Webseite, Kundenportal und dem internen Leitstand zusammenhängt, steht im nicht öffentlichen Repo der Webseite, Datei `UEBERBLICK.md`. Die Repos liegen nebeneinander in einem Ordner; das der Webseite heißt dort `ImagepixelAppWebseite`.
- Was Imagepixel auf der Webseite zum Programm verspricht und was es deshalb vor dem Start können muss:
  - Server-Sicherungen nur lesen, nie ändern
  - eigene Zugangsdaten je Gerät, im Kundenportal sperrbar
  - der Schlüssel entsteht auf dem Gerät und verlässt es nie, auch nicht beim Wiederherstellen
  - Sicherungen dedupliziert und komprimiert (Standard von restic)
  - Programm und Kundenportal zeigen gebuchte Größe und Belegung; Geräte ohne aktuelle Sicherung werden gemeldet
- Zum Download freigegeben wird eine Fassung erst, wenn sie fertig und signiert ist, die für macOS zusätzlich notarisiert.
