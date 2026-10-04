# KeepAlive

Aplicație mică de Windows care stă în tray și ține statusul „Online" în Teams,
Slack și altele asemenea, atunci când ești departe de calculator.

## Cum funcționează

- La fiecare **3 minute** verifică de cât timp nu ai atins tastatura sau mouse-ul
  (`GetLastInputInfo`).
- Dacă ai fost inactiv cel puțin **2 minute**, simulează o apăsare a tastei **F15**
  (`SendInput`). F15 nu e folosită de nicio aplicație uzuală, deci nu are efecte
  vizibile, dar Windows o contorizează ca activitate de utilizator.
- Dacă ai lucrat în ultimele 2 minute, nu face nimic.

## Utilizare

1. Pornește `KeepAlive.exe`. Apare iconița (monitor cu inel verde) în tray, lângă ceas.
2. Click dreapta pe iconiță:
   - **Interval** – la câte minute se simulează tasta (1, 2, 3, 5 sau 10; implicit 3).
   - **Previne sleep și stingerea ecranului** – bifat implicit. Cât timp rulează
     aplicația, Windows nu intră în sleep și nu stinge ecranul (prin
     `SetThreadExecutionState`). Debifează dacă vrei ca laptopul să adoarmă normal.
   - **Ieșire** – închide aplicația.

   Alegerile se țin minte în `%LOCALAPPDATA%\KeepAlive\settings.txt`.
3. Linia de sus din meniu arată intervalul curent și ora ultimei simulări.

Pentru diagnoză, intervalele se pot forța din linia de comandă (în secunde, fără
a fi salvate) și se poate scrie un log:

```
KeepAlive.exe --interval 20 --idle 10 --log C:\Temp\keepalive.log
```

Dacă vrei să pornească odată cu Windows, pune un shortcut către exe în
`shell:startup` (Win+R → `shell:startup`).

## Cerințe pe PC-ul de Windows

- Windows 10/11, 64-bit.
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
  (exe-ul este *framework-dependent*, ~170 KB). Dacă lipsește, Windows îți
  oferă link-ul de descărcare la prima pornire.

Pentru un exe care nu are nevoie de runtime instalat (self-contained, ~150 MB):

```
dotnet publish -c Release -o publish --self-contained true
```

## Build

Necesită .NET 8 SDK. Merge și de pe Linux/macOS (`EnableWindowsTargeting` e setat în `.csproj`).

```
dotnet publish -c Release -o publish
```

Rezultatul: `publish/KeepAlive.exe`.

Iconița din `Resources/app.ico` (exe + tray) se regenerează din
`Keep-Alive Monitor Icon.png` cu:

```
python3 tools/make_icons.py
```

## Limitări cunoscute

- Nu funcționează când ecranul este blocat (Win+L): Windows nu acceptă input
  simulat pe ecranul de blocare, iar Teams trece pe Away la blocare indiferent
  de input. Blocarea automată după inactivitate e însă prevenită, pentru că
  tasta simulată contează ca activitate. Dacă politica de companie blochează
  la 2 minute sau mai puțin, setează intervalul la 1 minut.
- Dacă o aplicație (de ex. un joc) are F15 mapată pe ceva, va reacționa la ea.
