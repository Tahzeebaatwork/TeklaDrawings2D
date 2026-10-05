# Step 0 — Task 1b staged Fit (evidence gate)

**Status:** Tooling ready; **operator Y/N still required** for first stage that introduces magenta `?`.

Screenshot evidence already locked for implementation Steps 1–3:

- Manual props-only sheet: no `?`, dense native H/V dims.
- Automated Fit sheet: `?` swarm, thin dims after `PurgeOldDimensions`.

## How to finish Step 0 (operator)

```powershell
cd c:\Users\ASUS\Desktop\2d_tekla\bin\x64\Release\net48
# Fresh manual [W10-175 - 1] with SP_M.CU_HARDWARE_PROPS_11X17, then:
.\TeklaExtractor.exe --civil-drawings --extract-only --mark W10-175 --fit-stage 1
# Reply Magenta ? Y/N — stop at first Y
```

Stages: 1 shell → 2 purge tags → 3 PlaceTiers → 4 formwork/park → 5 mark repair (needs `--enable-mark-repair`).

Implementation proceeds with **preserve-native dims** (Step 1) based on screenshot proof that purging native dims causes the elevation gap; mark repair stays default-off.
