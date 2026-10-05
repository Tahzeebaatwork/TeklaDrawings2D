# Step 4 — UI verify (after preserve-native Fit)

**Build:** `dotnet build -c Release -p:PlatformTarget=x64`  
**Exe:** `bin\Release\net48\TeklaExtractor.exe` (or `bin\x64\Release\net48\` if present)

## Recommended path (fresh manual props drawing)

1. In Tekla: delete automated `[W10-175 - 1]` if needed.
2. Create Cast Unit drawing with **only** `SP_M.CU_HARDWARE_PROPS_11X17` — confirm **no** magenta `?` and dense green dims.
3. Run Fit (mark repair stays off; native dims preserved when ≥8 sets):

```powershell
cd c:\Users\ASUS\Desktop\2d_tekla\bin\x64\Release\net48
.\TeklaExtractor.exe --civil-drawings --extract-only --mark W10-175
```

Console should include:
- `mark-repair=SKIP (default)` / `mark-repair SKIPPED`
- `preserve-native dims count=…` **or** `native dims count=… < 8 — recreate…`
- `UI refresh showDrawing=true`

## Pass / fail

| Check | Pass? |
|-------|-------|
| No magenta `?` on elevation | |
| Dense H/V dim elevations still visible (like manual) | |
| TOP IN FORM present | |
| Clicked mark is still Mark linked to part | |
| Second extract-only run: no dim duplicates / no mass mark rewrite | |

Peer wall (optional): same for `W10-78`.

## If `?` still appear after preserve-native

Finish Step 0 staged Fit (`--fit-stage 1..5`) and record first stage with Y — that stage is the remaining culprit (not mark repair by default).
