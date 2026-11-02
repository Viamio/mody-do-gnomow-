# Mody do Burglin' Gnomes

Zestaw modów (BepInEx 5) do gry **Burglin' Gnomes**.

| Mod | Co robi | Kto musi mieć |
|---|---|---|
| **Keep Progress On Loss** | Po przegranej zostaje dom, ulepszenia, meble i surowce w magazynie. Tracisz tylko ekwipunek z wyprawy. | host |
| **Mod Menu** | Przycisk **MODY** w menu głównym i **F1** w grze: lista modów, włączanie/wyłączanie, zmiana nazw i przycisk **Aktualizuj mody**. | każdy, kto chce menu |
| **Gnome AK** | Na start dnia host dostaje broń z gry przerobioną na „AK”: 30 naboi, przeładowanie, ogień ciągły, rosnący rozrzut. **F2** = kolejna broń (host). | host (ogień ciągły – każdy z modem) |
| **Muzyka w bazie** | Twoja muzyka gra w bazie i cichnie na wyprawie. Wrzuć pliki **.mp3 / .ogg / .wav** do folderu **`Muzyka`** w folderze gry (tworzy się sam). **F6** = następny utwór, **F7** = pauza. | każdy dla siebie |

## Instalacja i aktualizacja (jeden plik)

1. Pobierz paczkę: **[mody-do-gnomow.zip](https://github.com/Viamio/mody-do-gnomow-/archive/refs/heads/main.zip)**.
2. Otwórz ZIP i wyciągnij z niego **tylko plik `AKTUALIZUJ.bat`** do folderu gry
   (Steam → prawy klik na grze → Zarządzaj → Przeglądaj pliki lokalne).
3. Kliknij `AKTUALIZUJ.bat` dwa razy. Gotowe – uruchom grę normalnie przez Steam.

Aktualizacja – dwa sposoby:
- **w grze:** F1 (albo przycisk MODY) → **Aktualizuj mody** – gra sama się zamknie, zaktualizuje i uruchomi ponownie,
- **albo:** zamknij grę i kliknij `AKTUALIZUJ.bat` jeszcze raz.

Skrypt sam:
- znajdzie grę,
- zainstaluje BepInEx 5, jeśli go nie masz,
- pobierze najnowszy kod modów stąd i skompiluje go na Twoim komputerze (wbudowanym w Windows kompilatorem),
- zostawi wyłączone mody wyłączone.

> Windows może zapytać, czy uruchomić plik z internetu – kliknij „Więcej informacji” → „Uruchom mimo to”.

## Ustawienia

Pliki w `BepInEx\config\`:
- `pl.krystian.keepprogressonloss.cfg` – `Enabled`
- `pl.krystian.gnomeak.cfg` – magazynek, szybkostrzelność, rozrzut, odrzut
- `ModMenu.names.txt` – własne nazwy modów
- `pl.krystian.basemusic.cfg` – głośność, losowa kolejność, czas wyciszania, muzyka w menu głównym

## Odinstalowanie

Usuń z folderu gry folder `BepInEx` oraz pliki `winhttp.dll` i `doorstop_config.ini`
albo w Steamie: Właściwości → Zainstalowane pliki → Sprawdź spójność plików.

## Uwagi

- Gra nie ma anty-cheata – mody nie grożą banem na Steamie.
- Repozytorium zawiera tylko kod modów, bez plików gry.
