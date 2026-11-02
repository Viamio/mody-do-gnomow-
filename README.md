# Mody do Burglin' Gnomes

Zestaw modów (BepInEx 5) do gry **Burglin' Gnomes**.

| Mod | Co robi | Kto musi mieć |
|---|---|---|
| **Keep Progress On Loss** | Po przegranej zostaje dom, ulepszenia, meble i surowce w magazynie. Tracisz tylko ekwipunek z wyprawy. | host |
| **Mod Menu** | Przycisk **MODY** w menu głównym i **F1** w grze: lista modów, włączanie/wyłączanie, zmiana nazw, **ustawienia każdego moda** i przycisk **Aktualizuj mody**. | każdy, kto chce menu |
| **Gnome AK** | Na start dnia host dostaje broń z gry przerobioną na „AK”: 30 naboi, przeładowanie, ogień ciągły, rosnący rozrzut. **F2** = kolejna broń (host). | host (ogień ciągły – każdy z modem) |
| **Muzyka i radio w bazie** | W bazie stoi radio z gry i gra **radio internetowe** – dźwięk dochodzi z radia (bliżej = głośniej). Albo Twoje pliki **.mp3/.ogg/.wav** z folderu **`Muzyka`**. Cichnie na wyprawie. **F6** = następna stacja/utwór, **F7** = pauza, **F8** = radio ⇄ pliki, **F9** = postaw radio przed sobą. | każdy, kto chce słyszeć |
| **Kopia zapisu** | Przed każdą wyprawą host robi kopię zapisu gry (`AppData\LocalLow\...\SaveGames\Kopie`), trzyma 15 ostatnich. | host |
| **Dziennik i statystyki** | Na koniec dnia tabelka: kto ile gnomium oddał i kto ile razy zginął. Historia wszystkich dni pod **F3**. | każdy (gnomium liczy host) |
| **Wyzwania dnia** | Losowy **modyfikator dnia** (ciemność, mgła, dziadek z kawą, zaspany dziadek, niska grawitacja, złota gorączka), **wściekły dziadek** (hałas = szybszy), **alarm** po serii hałasów, **skarb dnia** – świecący przedmiot wart 5×. | host (efekty wizualne – każdy z modem) |
| **Rozwój gnoma** | **F4**: Żołędzie za każdy dzień, **klasy** (Złodziej, Siłacz, Zwiadowca, Saper), **umiejętności** (szybkość, skok, stamina, siła, hak), ulepszenia drużyny (**plecak** +1 miejsce, **ciche kroki**), **osiągnięcia**. | każdy dla siebie (drużynowe – host) |

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
- `pl.krystian.basemusic.cfg` – lista stacji radiowych (`Stations`), głośność, zasięg słyszenia radia, skala modelu, muzyka w menu głównym

## Odinstalowanie

Usuń z folderu gry folder `BepInEx` oraz pliki `winhttp.dll` i `doorstop_config.ini`
albo w Steamie: Właściwości → Zainstalowane pliki → Sprawdź spójność plików.

## Uwagi

- Gra nie ma anty-cheata – mody nie grożą banem na Steamie.
- Repozytorium zawiera tylko kod modów, bez plików gry.
