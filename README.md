# Fan Controller

Segue il tema chiaro/scuro di Windows (Impostazioni › Personalizzazione › Colori).

Controllo ventole minimalista per Windows 11 (Gigabyte Z790 AORUS ELITE X WIFI7 · ITE IT8689E · NZXT Kraken).

## Architettura

| Processo | Ruolo | Quando è in memoria |
|---|---|---|
| `FanCtl.exe` | Controllo: sensori, curve, tray icon. Nessun framework UI (Win32 puro), un thread, EcoQoS. Richiede amministratore (driver PawnIO). | Sempre |
| `FanCtl.UI.exe` | Finestra WPF (Mica, editor curve). | Solo mentre è aperta: chiudendola il processo termina |

Comunicazione: memoria condivisa (`Local\FanCtl.Snapshot`, scritta solo con la UI aperta) + evento per il ricaricamento della config.
Config: `%LOCALAPPDATA%\FanCtl\config.json`.

Con la UI chiusa il servizio aggiorna **solo** l'hardware usato dalle curve (+ CPU per la protezione termica).

## Sicurezza

- Sensore della curva non disponibile → 100%.
- CPU Package ≥ 95 °C → tutte le ventole controllate al 100%.
- Uscita / logoff / crash → le ventole toccate tornano al controllo BIOS/firmware.
- Rampa: +20%/s in salita, −5%/s in discesa; isteresi 2 °C.

## Build

```
dotnet build FanCtl.sln -c Release
```

Output in `bin\Release\`. Avvia `FanCtl.exe` (UAC). `FanCtl.exe --tray` parte senza aprire la finestra; `FanCtl.exe --dump file.txt` scrive l'elenco dei sensori.

**Chiudi NZXT CAM e FanControl**: due programmi che scrivono le stesse ventole si sovrascrivono a vicenda.
