# Emulator HC2000

[English](README.md) · **Română**

Un emulator pentru **ICE Felix HC2000**, calculatorul personal românesc din anii '90, scris de la zero în C# pe .NET 10.
HC2000 este compatibil ZX Spectrum și are o unitate de dischetă de 3,5" încorporată (controller Intel 8272). Rulează
CP/M 2.2 pe ecranul grafic, la 64 x 24 de caractere, cu un font de 4x8.

Scopul este o mașină fidelă: ROM-urile, BIOS-ul și programele CP/M originale rulează nemodificate, așa cum le rulează
hardware-ul real. Emulatorul vine cu o aplicație desktop pentru Windows și Linux și cu un set de unelte Z80.

## Instalare

### Windows 10 / 11 (x64)

Deschide **PowerShell** și rulează:

```powershell
irm https://raw.githubusercontent.com/mteletin/hc2026/main/install.ps1 | iex
```

Scriptul descarcă ultimul `setup.exe` din [Releases](https://github.com/mteletin/hc2026/releases), îi verifică SHA-256
cu amprenta (digest) publicată de GitHub și îl instalează pentru utilizatorul tău. Nu are nevoie de drepturi de
administrator.

- Pentru instalare manuală, descarcă `hc2000-emu-<versiune>-setup.exe` din Releases și rulează-l. Instalatorul nu este
  semnat, așa că Windows SmartScreen poate afișa „Windows protected your PC”: alege **More info**, apoi **Run anyway**.
- Pentru o anumită versiune: `$env:HC2000_VERSION = '0.1.118'` înainte de comandă.
- Pentru a vedea asistentul de instalare în locul instalării silențioase: `$env:HC2000_WIZARD = '1'` înainte de comandă.

| ce | unde |
|---|---|
| programul | `%LOCALAPPDATA%\Programs\HC2000` (în asistent poți alege alt dosar) |
| scurtături | meniul Start și desktopul: **HC2000 Emu** |
| datele tale: setări, ROM-uri, imagini de dischetă, casetă și snapshot | `%APPDATA%\hc2000` |
| dezinstalare | Settings > Apps > **HC2000 Emu** |

### Ubuntu (x64)

```bash
curl -fsSL https://raw.githubusercontent.com/mteletin/hc2026/main/install.sh | bash
```

Scriptul descarcă ultimul `linux-x64.run` din Releases, îi verifică SHA-256 cu amprenta publicată de GitHub și îl
rulează. Nu are nevoie de `sudo`: totul se instalează în dosarul tău personal. .NET și SDL2 sunt incluse. Aplicația
mai are nevoie de biblioteca de sistem `libfontconfig1`: Ubuntu Desktop o are; pe o instalare minimă
sau de server, rulează întâi `sudo apt install libfontconfig1`.

- Opțiunile merg la instalator: `... | bash -s -- --prefix ~/apps/hc2000` instalează în alt dosar, iar
  `... | bash -s -- --uninstall` șterge programul.
- Pentru o anumită versiune: `... | HC2000_VERSION=0.1.118 bash`.
- Pentru instalare manuală, descarcă `hc2000-emu-<versiune>-linux-x64.run` din Releases și rulează
  `bash hc2000-emu-<versiune>-linux-x64.run`.

| ce | unde |
|---|---|
| programul | `~/.local/opt/hc2000` |
| lansare | comanda `hc2000` (în `~/.local/bin`) și **HC2000** în meniul de aplicații |
| datele tale: setări, ROM-uri, imagini de dischetă, casetă și snapshot | `~/.config/hc2000` |

Dacă după prima instalare comanda `hc2000` nu este găsită, ieși din sesiune și intră din nou (Ubuntu adaugă
`~/.local/bin` în `PATH` la autentificare) sau rulează `~/.local/bin/hc2000`.

### Actualizări și datele tale

Dacă rulezi din nou oricare instalator, programul se actualizează pe loc. Setările, ROM-urile și imaginile de dischetă,
casetă și snapshot nu sunt niciodată modificate sau șterse, nici măcar la dezinstalare. ROM-urile și discul de sistem
CP/M `CPM22-HC.DSK` sunt copiate în dosarul tău de date doar dacă nu există deja acolo.

## Folosirea emulatorului

- **Machines (mașini):** creezi una sau mai multe mașini HC2000 (**New machine**), fiecare cu configurația și setul ei
  de ROM-uri, și le resetezi sau le repornești.
- **Media and disks (medii și discuri):** unitățile de dischetă A: și B: (imagini Extended CPC DSK, `.DSK`), imagini de
  hard disk și casetă.
- **Snapshots:** salvezi și restaurezi starea completă a unei mașini (`.hcs`).
- **Keyboard and paste (tastatură și lipire):** alegi maparea **Symbolic** (caracterul tastat pe PC este cel pe care îl
  primește HC2000) sau **Positional** (poziția fizică a tastei) și lipești text în mașină.
- **Debugger:** dezasamblare cu etichete, puncte de oprire (inclusiv la schimbarea paginilor de memorie), vedere a
  memoriei pe pagini, un panou pentru controllerul de dischetă și înregistrarea hărții de execuție.
- **Settings (setări):** profilul mașinii și setul de ROM-uri, viteza, scara afișării, sunetul, maparea tastelor și
  dosarele pentru imaginile tale de dischetă, casetă și snapshot.

## Unelte în linie de comandă

Unealta `hc` lucrează cu imagini HC2000 și cu cod Z80. Din sursă o rulezi cu
`dotnet run --project src/Hc2000.Tools -- <subcomandă> ...`:

| subcomandă | rol |
|---|---|
| `asm` | asamblor Z80 cu sintaxă Zilog (reasamblează dezasamblările ROM-urilor octet cu octet) |
| `disasm` | dezasamblor cu urmărirea execuției, fișiere de control și hărți de execuție |
| `dsk` | imagini de dischetă DSK |
| `hdd` | imagini de hard disk |
| `fs` | sistemele de fișiere CP/M și HC BASIC din imagini |
| `tap`, `tape` | imagini de casetă |
| `rom` | verificări de ROM |
| `paging` | paginarea memoriei |

## Compilare din sursă

Ai nevoie de [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), iar pe Linux de `libfontconfig1` pentru
aplicație și testele ei.

```bash
git clone https://github.com/mteletin/hc2026.git
cd hc2026
dotnet build Hc2000.slnx
dotnet run --project src/Hc2000.App          # aplicația desktop
dotnet test Hc2000.slnx --filter "speed!=slow"   # testele, fără cele lente
dotnet test Hc2000.slnx --filter speed=slow      # cele lente: ZEXALL, porniri complete
```

- Unele teste au nevoie de fișiere care nu sunt în acest depozit (seturi de teste ale altor autori, discurile cu jocuri
  BASIC), așa că o rulare din acest depozit arată teste sărite și poate arăta eșecuri la aceste teste.
- Sunetul folosește SDL2 2.32.10, iar aplicația cere exact această versiune. Instalatoarele o includ. O compilare din
  sursă rulează fără sunet până când copiezi `deploy/emu/sdl2/SDL2.dll` (Windows) sau
  `deploy/emu/sdl2/linux-x64/libSDL2-2.0.so.0` (Linux) în dosarul de ieșire al aplicației, sub
  `runtimes/win-x64/native/` sau `runtimes/linux-x64/native/`.

## Ce conține acest depozit

| dosar | conținut |
|---|---|
| `src/Hc2000.Z80` | nucleul procesorului Z80, folosit de toate celelalte |
| `src/Hc2000.Machine` | HC2000 propriu-zis: memorie și paginare, video, tastatură, controller de dischetă, serial, Interface 1 |
| `src/Hc2000.App` | aplicația desktop Avalonia și debuggerul ei |
| `src/Hc2000.Asm`, `src/Hc2000.Disasm` | asamblorul și dezasamblorul |
| `src/Hc2000.Tools` | unealta `hc` în linie de comandă |
| `bios/` | BIOS-ul HC2000 dezasamblat, care se reasamblează octet cu octet |
| `roms/` | imaginile ROM ale HC2000; vezi `roms/README.md` |
| `disks/cpm/` | discuri de sistem CP/M 2.2; vezi `disks/README.md` |
| `tests/`, `tools/` | suitele de teste și uneltele de compilare |
| `deploy/emu/` | instalatoarele pentru Windows și Ubuntu |
| `docs/notes/` | note tehnice |

Manualele și schemele originale, discurile cu jocuri BASIC și codul de referință al altor autori nu sunt publicate aici.

## Versiuni

Fiecare versiune stabilă este publicată aici într-un singur sens: un instantaneu al sursei, o etichetă `v<versiune>` și
un GitHub Release cu instalatoarele pentru Windows și Ubuntu. Dezvoltarea are loc într-un depozit separat, așa că
pull request-urile de aici nu sunt preluate. Versiunea curentă este în [VERSION.md](VERSION.md).
