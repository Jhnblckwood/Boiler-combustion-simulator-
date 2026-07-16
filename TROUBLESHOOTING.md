# Troubleshooting

## Studio 5000 / Logix Designer crash when opening the boiler PLC project (`.ACD`)

### Symptom

Logix Designer crashes ~1 second after opening the boiler project file, with:

```
Application Path: ...\Studio 5000\Logix Designer\ENU\v34\Bin\LogixDesigner.exe
Version: V34.01.00 (Release)
Error 0x8004203b (-2147213253)
RxE_INVALID_INTERNAL_STATE - Invalid software state due to inconsistency found.

One project file is currently open:
    C:\USERS\<user>\ONEDRIVE - ...\DESKTOP\...\_98500509_001_012_RH150_14a.ACD
        Work Path: C:\Users\<user>\AppData\Local\Temp\RSLogix5000.Temp\AB_6207\AB_755D
        Time open: 1 second
```

### Root cause

This is an environment/version problem, not (usually) a damaged file:

1. **Version mismatch.** The project is a **V20.05.00 (build 3489.005)** project (internal
   `RxController` object version 68). The crash log shows it being opened in **Studio 5000
   Logix Designer V34.01.00**. Opening a v20 project in v34 forces an automatic conversion;
   a hard "invalid software state — inconsistency found" crash during that conversion is a
   state failure, not the normal "wrong version" dialog.

2. **The `.ACD` is stored on OneDrive** (`ONEDRIVE - ...\DESKTOP\...`). Rockwell warns
   against opening `.ACD` files directly from OneDrive/Dropbox/Google Drive. With OneDrive
   "Files On‑Demand," the on-disk file can be a partially-hydrated placeholder, and OneDrive
   can re-sync or lock it mid-open. Logix Designer random-access reads the `.ACD`, so bytes
   changing/incomplete underneath it produce exactly this symptom: opens, reads inconsistent
   internal state, aborts within a second.

### Recovery steps (in order)

1. **Get the file off OneDrive before opening it.**
   In File Explorer, right‑click the `.ACD` → **"Always keep on this device"** and wait for a
   solid green check (not a cloud icon). Then **copy** it to a plain local folder such as
   `C:\Logix\Projects\`. Open it only from there — never directly from the OneDrive/Desktop
   folder.

2. **Open it with the matching version — V20, not V34.**
   It's a v20 project, and V20 is installed on the machine (it saved the file cleanly the same
   day it crashed in V34). Opening it in its native version avoids the conversion path
   entirely. To move it to V34, get it opening cleanly in V20 first, then in V34 use
   **File → Open** on a known-good *local* copy and let it convert — do not convert straight
   off OneDrive.

3. **Clear the stale temp work path.**
   Close every Studio 5000 / Logix Designer instance, then delete the contents of
   `C:\Users\<user>\AppData\Local\Temp\RSLogix5000.Temp\` (the `AB_####\AB_####` leftovers
   from the crashed session can wedge the next open). Reboot to release any OneDrive/AppData
   file locks.

4. **If it still won't open,** the project data itself appears structurally complete, so
   recovery odds are good:
   - Try the local copy in V20 first.
   - If V20 also faults, restore the last known-good revision from OneDrive **Version history**
     (right‑click the file on OneDrive → *Version history*). It was saved cleanly in V20, so a
     prior revision should be intact.

### Prevention

- Keep working `.ACD` files on a **local, non-synced** path (e.g. `C:\Logix\Projects\`).
  Use OneDrive/version control only for archival copies you copy *out* before opening.
- Open a project in the **Logix Designer major version that created it**. Only convert to a
  newer version deliberately, from a local copy, and keep the original.
