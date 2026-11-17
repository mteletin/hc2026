# Emulator HC2000

[English](README.md) · **Română**

Un emulator pentru **ICE Felix HC2000**, calculatorul personal românesc din anii '90, scris de la zero în C# pe .NET 10.
HC2000 este compatibil ZX Spectrum și are o unitate de dischetă de 3,5" încorporată (controller Intel 8272). Rulează
CP/M 2.2 pe ecranul grafic, la 64 x 24 de caractere, cu un font de 4x8.

Scopul este o mașină fidelă: ROM-urile, BIOS-ul și programele CP/M originale rulează nemodificate, așa cum le rulează
hardware-ul real. Peste ea, un **HC2000 extins**, pe care îl activezi bucată cu bucată, adaugă ce mașina reală nu a avut:
hard disk-uri, megaocteți de RAM paginat, o placă VGA ca al doilea ecran, sunet, Z80-uri în plus pentru programele CP/M,
rețea și un terminal SSH. Emulatorul vine cu o aplicație desktop pentru Windows și Linux, cu uneltele `hc` din linia de
comandă pentru imagini de disc, casetă și ROM, cu un asamblor și un dezasamblor Z80 și cu programele HC2000 ca surse
lizibile.

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
- Pe Windows, admiterea nativă a imaginilor de disc necesită o unitate locală fixă NTFS. Permisiunile moștenite obișnuite sunt acceptate; căile de rețea și cele prin puncte de reanalizare sunt refuzate.
- O detecție malware în Defender este diferită de avertismentul SmartScreen despre editor. Raportează versiunea exactă a instalatorului și numele amenințării pentru investigare; nu dezactiva antivirusul.
- Pentru o anumită versiune: `$env:HC2000_VERSION = '0.1.118'` înainte de comandă.
- Pentru a vedea asistentul de instalare în locul instalării silențioase: `$env:HC2000_WIZARD = '1'` înainte de comandă.

| ce | unde |
|---|---|
| programul | `%LOCALAPPDATA%\Programs\HC2000` (în asistent poți alege alt dosar) |
| scurtături | meniul Start și desktopul: **HC2000 Emu** |
| uneltele din linia de comandă | `hc`, adăugat în `PATH`-ul utilizatorului |
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
| uneltele din linia de comandă | `hc` (în `~/.local/bin`) |
| datele tale: setări, ROM-uri, imagini de dischetă, casetă și snapshot | `~/.config/hc2000` |

Dacă după prima instalare comanda `hc2000` nu este găsită, ieși din sesiune și intră din nou (Ubuntu adaugă
`~/.local/bin` în `PATH` la autentificare) sau rulează `~/.local/bin/hc2000`.

### Actualizări și datele tale

Dacă rulezi din nou oricare instalator, programul se actualizează pe loc. Setările, ROM-urile și imaginile de dischetă,
casetă și snapshot nu sunt niciodată modificate sau șterse, nici măcar la dezinstalare. ROM-urile și discul de sistem
CP/M `CPM22-HC.DSK` sunt copiate în dosarul tău de date doar dacă nu există deja acolo.

## Folosirea emulatorului

- **Machines (mașini):** creezi una sau mai multe mașini HC2000 (**New machine**), fiecare în tabul ei, cu configurația
  și setul ei de ROM-uri, și le resetezi sau le repornești.
- **Media and disks (medii și discuri):** unitățile de dischetă A: și B: (imagini Extended CPC DSK, `.DSK`), imagini de
  hard disk și casetă.
- **Exploratorul de medii:** răsfoiești dosarele tale și imaginile de dischetă, hard disk și casetă ca pe niște dosare,
  vezi conținutul fișierelor și copiezi fișiere între o imagine și PC.
- **Biblioteca de discuri:** un catalog al imaginilor tale de dischetă și hard disk (nume, format, conținut, sumă de
  control), cu căutare, montare dintr-o singură acțiune și schimbare în timp ce mașina rulează.
- **Casetofonul:** un casetofon pe linia EAR/MIC, cu redare, înregistrare, derulare și contor. Citește și scrie TAP, TZX,
  PZX, CSW și WAV.
- **Snapshots:** salvezi și restaurezi starea completă a unei mașini (`.hcs`), inclusiv memoria paginată mare și conținutul HDD. Restaurarea unui snapshot nu reactivează legăturile de scriere către fișierele de pe PC.
- **Keyboard and paste (tastatură și lipire):** alegi maparea **Symbolic** (caracterul tastat pe PC este cel pe care îl
  primește HC2000) sau **Positional** (poziția fizică a tastei) și lipești text în mașină.
- **Text mai fin (opțional):** o opțiune de afișare care desenează cele 32 de coloane din BASIC și cele 64 din CP/M cu un
  font mai clar. Mașina rămâne neschimbată; dezactivează opțiunea ca să vezi pixelii originali.
- **Debugger:** dezasamblare cu etichete, puncte de oprire (inclusiv la schimbarea paginilor de memorie), vedere a
  memoriei pe pagini, un panou pentru controllerul de dischetă și înregistrarea hărții de execuție.
- **Settings (setări):** profilul mașinii și setul de ROM-uri, viteza, scara afișării, sunetul, maparea tastelor și
  dosarele pentru imaginile tale de dischetă, casetă și snapshot.

## HC2000 extins

Instalările noi și instanțele noi de mașină folosesc profilul **full workstation**: CP/M pornește de pe C:, cu volumele de hard disk C:–F:, 1 GiB de RAM paginat, consola VGA X2, sunet X3, două drivere Z80 auxiliare și rețea X6 cu poarta de rețea activată. Mașinile deja salvate își păstrează configurațiile.

Alege presetul **Classic** pentru mașina originală fidelă și lucrul cu BASIC și dischete. Din **Hardware** (sau cu opțiunile corespunzătoare din linia de comandă) poți alege alte profiluri și extensii. Stația completă rezervă 1 GiB de RAM pentru mașina emulată; Classic folosește mult mai puțină memorie pe PC. O schimbare de hardware pornește o mașină nouă.

| îmbunătățire | ce primești |
|---|---|
| hard disk-uri | unitățile CP/M C: până la F: pe imagini de hard disk, instalate cu `HDINST` de pe discheta CP/M pentru hard disk livrată cu emulatorul; `HDFMT` șterge un volum |
| memorie paginată | 1 GiB de RAM paginat în stația completă, cu profiluri mai mici disponibile și discul RAM **M:** sub CP/M (`MFMT` îl formatează, `RAMSTAT` arată spațiul liber) |
| caseta ca suport de stocare | o unitate CP/M pe o imagine de casetă, prin portul de control al casetofonului (`TAPEINIT`), cu confirmare sau renunțare la modificări |
| procesor mai rapid | viteza Z80 de 2x, 4x sau 8x |
| X2 VGA | un afișaj VGA grafic și text; stația completă îl folosește drept ecran principal CP/M de 640x480. Sub CP/M, `X2INST` o face dispozitiv: `STAT CON:=UC1:` mută consola pe ea, la 80 de coloane, `STAT CON:=CRT:` o aduce înapoi, iar `LST:` poate tipări pe ea, ca pe un al doilea monitor |
| sunet X3, matematică X4 | o placă de sunet (cu un mic player MOD, `X3MOD`) și un coprocesor matematic |
| Z80-uri auxiliare | unități Z80 auxiliare cărora un program CP/M le poate da de lucru (numărul depinde de profil): `SWCYCLE` trimite o sarcină și verifică răspunsul, iar demonstrațiile de 3D wireframe și de căutare a drumului pe hartă își fac calculele acolo |
| CP/NET | un adaptor de rețea cu CP/NET pentru CP/M 2.2: `NETINST`, `NETLOGIN` și `NETMAP` leagă un disc de pe server la o literă locală |
| rețea X6 și SSH | vezi mai jos |

`SYSINFO` îți spune, din CP/M, pe ce mașină ești și ce are. `DRVSEL` arată ce unități răspund, iar `SUM` afișează
mărimea și suma de control a unui fișier.

Cu emulatorul vine și o imagine de hard disk **C: cu unelte**, gata de folosit, cu aceste comenzi și o listă a ce conține,
alături de volumele D:, E: și F:.

### SSH din CP/M

Cu placa de rețea X6, CP/M poate deschide o sesiune SSH către un calculator real și poate rula acolo un program de
terminal Linux: un shell, Midnight Commander, editoare. Criptarea și stiva de rețea rulează în emulator, pe o unitate Z80
auxiliară; CP/M doar trimite și primește text simplu printr-un API simplu, iar terminalul transformă ieșirea VT100/xterm de
la distanță în culorile, mișcările de cursor și modul ecran complet ale HC2000.

1. Verifică poarta de rețea: comutatorul din bara de stare sau **Ctrl+Shift+O**. Instanțele noi full workstation o activează; mașinile salvate își păstrează setarea. Mașina ajunge în rețea prin PC-ul tău, care îi ține loc de router.
2. Setează o singură dată conexiunea implicită în **Network · X6 gateway** (`user@host`, opțional un port și o cheie;
   parolele nu se salvează niciodată) sau pornește aplicația cu `--ssh-default user@host[:port][,key:name]`.
3. În CP/M scrie `SSH`. Pornește singur driverul de rețea și se autentifică. `SSH /M` afișează în schimb meniul de
   conexiuni, iar `SSH /H` folosește ecranul propriu al HC2000, de 64 de coloane, în locul celui de 80 x 30 al X2 VGA.

`--network-gateway` activează poarta la pornirea aplicației.

## Uneltele `hc` din linia de comandă

Instalatoarele pun și comanda `hc` în `PATH` (`~/.local/bin/hc` pe Ubuntu, `<dosarul de instalare>\hc` pe Windows).
Fiecare comandă are `--help`.

| comandă | ce face |
|---|---|
| `hc fs check` | verifică sistemul de fișiere al unei imagini de disc CP/M și spune ce nu e în regulă, fără să o modifice |
| `hc fs survey` | același lucru pentru un dosar întreg de imagini: sănătoasă, deteriorată sau imposibil de verificat |
| `hc fs locate` | găsește un fișier CP/M după nume într-un dosar de imagini |
| `hc fs duplicates` | găsește fișiere identice între imagini, chiar sub alte nume sau în alte zone de utilizator |
| `hc fs variants` | găsește fișiere cu același nume, dar cu conținut diferit |
| `hc fs compare` | arată fișierele care diferă între două imagini |
| `hc fs export` / `hc fs restore` | exportă toate fișierele unui disc într-un dosar nou de pe PC, cu un inventar, și reface mai târziu din el un disc de 40 sau 80 de piste |
| `hc fs from-folder` | face un disc de date CP/M nou dintr-un dosar de fișiere |
| `hc fs repack` | mută fișierele unui disc pe un disc nou de 40 sau 80 de piste |
| `hc dsk`, `hc hdd`, `hc tap`, `hc tape`, `hc rom` | creează, inspectează și modifică imagini de dischetă, hard disk, casetă și ROM |
| `hc asm`, `hc disasm` | asamblorul Z80 (sintaxă Zilog, macrouri, `.bin`/Intel HEX, listing, simboluri) și dezasamblorul cu urmărire a codului |
| `hc backup` | face o copie de siguranță a fișierelor de disc, casetă și snapshot într-o arhivă cu manifest, o verifică și o restaurează |
| `hc settings` | arată și restaurează setările implicite ale aplicației, păstrând o copie a celor vechi |
| `hc support` | scrie un raport local pentru suport (versiune, platformă, verificarea configurației), fără nimic privat în el |

Comenzile care doar inspectează nu modifică niciodată imaginile tale; cele care scriu creează fișiere noi.

## Programele incluse pentru mașina emulată

Depozitul conține emulatorul C#, exemplele publicate pentru BIOS și dispozitive și programele compilate pentru mașina
emulată. Compilările publice folosesc programele și imaginile de disc din `build/guest-payload/` și verifică manifestul
SHA-256 înainte de a pregăti discurile stației de lucru.

## Compilare din sursă

Ai nevoie de [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), iar pe Linux de `libfontconfig1`.

```bash
git clone https://github.com/mteletin/hc2026.git
cd hc2026
dotnet build Hc2000.slnx
dotnet run --project src/Hc2000.App
```

Sunetul folosește SDL2 2.32.10, iar aplicația cere exact această versiune. Instalatoarele o includ. Pentru o compilare din
sursă, pune SDL2 2.32.10 ([libsdl.org](https://www.libsdl.org/)) în dosarul de ieșire al aplicației, sub
`runtimes/win-x64/native/` (`SDL2.dll`) sau `runtimes/linux-x64/native/` (`libSDL2-2.0.so.0`); fără ea, aplicația rulează
fără sunet.
