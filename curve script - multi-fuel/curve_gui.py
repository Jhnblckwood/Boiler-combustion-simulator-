"""
curve_gui.py
------------

A tiny drag-and-drop GUI in front of the curve extractors.

Drop a Studio 5000 file onto the window:

    * ``.L5K``  -> both fuels' stored curve sets (Fuel 1 and Fuel 2), read from
      the ArrayMgmt_F* tags — see :mod:`fuel_curves`.
    * ``.ACD``  -> tag structure only (the ACD library can't read values).

Each fuel table has rows purge, LtOff, 1..16 and columns Air, Fuel Act1, FGR,
VFD, and O2. Fuel Act1's purge cell and the whole O2 purge/LtOff are left blank.

Real drag-and-drop needs the optional ``tkinterdnd2`` package. If it isn't
installed the window still works — the drop zone becomes a click-to-browse
button — so the app runs anywhere Python + tkinter is available.
"""

from __future__ import annotations

import os
import queue
import threading
import tkinter as tk
from tkinter import filedialog, ttk

import curve_extractor as ce
import fuel_curves as fc

# Optional drag-and-drop support.
try:
    from tkinterdnd2 import DND_FILES, TkinterDnD

    _HAS_DND = True
except Exception:  # noqa: BLE001 - any import problem falls back to browse
    _HAS_DND = False


APP_TITLE = "Fuel/Air Curve Reader"


class CurveApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title(APP_TITLE)
        self.root.geometry("720x620")
        self.root.minsize(560, 480)

        self._result_queue: "queue.Queue" = queue.Queue()

        self._build_widgets()
        self.root.after(100, self._poll_queue)

    # -- layout -------------------------------------------------------------
    def _build_widgets(self):
        pad = {"padx": 12, "pady": 6}

        header = ttk.Label(
            self.root,
            text="Drop a Studio 5000 .ACD or .L5K file below",
            font=("Segoe UI", 13, "bold"),
        )
        header.pack(anchor="w", **pad)

        # Drop zone --------------------------------------------------------
        self.drop = tk.Label(
            self.root,
            text=self._drop_prompt(),
            relief="ridge",
            borderwidth=2,
            height=4,
            bg="#eef3f8",
            fg="#33475b",
            cursor="hand2",
        )
        self.drop.pack(fill="x", padx=12, pady=(0, 6))
        self.drop.bind("<Button-1>", lambda _e: self._browse())

        if _HAS_DND:
            self.drop.drop_target_register(DND_FILES)
            self.drop.dnd_bind("<<Drop>>", self._on_drop)

        # Status line ------------------------------------------------------
        self.status = ttk.Label(self.root, text="Ready.", foreground="#555")
        self.status.pack(anchor="w", padx=12)

        # Results table ----------------------------------------------------
        table_frame = ttk.Frame(self.root)
        table_frame.pack(fill="both", expand=True, padx=12, pady=8)

        self.tree = ttk.Treeview(table_frame, show="headings", height=18)
        vsb = ttk.Scrollbar(table_frame, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=vsb.set)
        self.tree.pack(side="left", fill="both", expand=True)
        vsb.pack(side="right", fill="y")

        # Notes area -------------------------------------------------------
        self.notes = tk.Text(self.root, height=4, wrap="word", bg="#fbfbf7",
                             relief="flat")
        self.notes.pack(fill="x", padx=12, pady=(0, 12))
        self.notes.configure(state="disabled")

    def _drop_prompt(self) -> str:
        if _HAS_DND:
            return "\n  Drag a .ACD / .L5K file here\n  (or click to browse)"
        return "\n  Click here to choose a .ACD / .L5K file\n"

    # -- file intake --------------------------------------------------------
    def _browse(self):
        path = filedialog.askopenfilename(
            title="Choose a Studio 5000 project",
            filetypes=[
                ("Studio 5000 files", "*.ACD *.acd *.L5K *.l5k"),
                ("ACD project", "*.ACD *.acd"),
                ("L5K export", "*.L5K *.l5k"),
                ("All files", "*.*"),
            ],
        )
        if path:
            self._load(path)

    def _on_drop(self, event):
        # tkinterdnd2 hands back a brace-wrapped, possibly multi-file string.
        raw = event.data.strip()
        path = self._first_path(raw)
        if path:
            self._load(path)

    @staticmethod
    def _first_path(raw: str) -> str:
        raw = raw.strip()
        if raw.startswith("{"):
            end = raw.find("}")
            return raw[1:end] if end != -1 else raw[1:]
        return raw.split()[0] if raw else ""

    # -- processing (off the UI thread) ------------------------------------
    def _load(self, path: str):
        if not os.path.isfile(path):
            self._set_status(f"Not a file: {path}", error=True)
            return
        self._set_status(f"Reading {os.path.basename(path)} …")
        self.drop.configure(text="\n  Working…\n")
        thread = threading.Thread(target=self._worker, args=(path,), daemon=True)
        thread.start()

    def _worker(self, path: str):
        try:
            # .L5K -> both fuels' stored curves; .ACD -> structure only.
            if os.path.splitext(path)[1].lower() == ".l5k":
                data = fc.extract_multifuel_l5k(path)
            else:
                data = ce.extract(path)
            self._result_queue.put(("ok", data))
        except (ce.ExtractError, fc.ExtractError) as exc:
            self._result_queue.put(("err", str(exc)))
        except Exception as exc:  # noqa: BLE001 - keep the UI alive
            self._result_queue.put(("err", f"Unexpected error:\n{exc}"))

    def _poll_queue(self):
        try:
            while True:
                kind, payload = self._result_queue.get_nowait()
                if kind == "ok":
                    self._show_result(payload)
                else:
                    self._set_status("Failed to read file.", error=True)
                    self._show_notes([payload])
                    self._clear_table()
                self.drop.configure(text=self._drop_prompt())
        except queue.Empty:
            pass
        self.root.after(100, self._poll_queue)

    # -- rendering ----------------------------------------------------------
    def _show_result(self, data):
        if isinstance(data, fc.MultiFuelData):
            table = fc.build_combined_table(data)
            self._render_table(table, label_width=140)
            who = data.controller_name or "(unknown)"
            self._set_status(
                f"{data.source_file}  •  controller: {who}  •  L5K (both fuels)"
            )
            self._show_notes(list(data.notes))
            return

        table = ce.build_table(data)
        self._render_table(table)
        who = data.controller_name or "(unknown)"
        self._set_status(
            f"{data.source_file}  •  controller: {who}  •  {data.file_kind}"
        )
        self._show_notes(list(data.notes))

    def _render_table(self, table: dict, label_width: int = 90):
        self._clear_table()
        columns = ["__label__"] + table["columns"]
        self.tree["columns"] = columns

        self.tree.heading("__label__", text=table["corner"])
        self.tree.column("__label__", width=label_width, anchor="center")
        for col in table["columns"]:
            self.tree.heading(col, text=col)
            self.tree.column(col, width=100, anchor="center")

        self.tree.tag_configure("section", background="#e8eef5")
        self.tree.tag_configure("fuel", background="#d5e3d5", font=("Segoe UI", 10, "bold"))
        for row in table["rows"]:
            values = [row["label"]] + [row["cells"][c] for c in table["columns"]]
            if row.get("is_header"):
                tag = "fuel"
            elif row["label"] in ("purge", "LtOff"):
                tag = "section"
            else:
                tag = ""
            self.tree.insert("", "end", values=values, tags=(tag,))

    def _clear_table(self):
        for item in self.tree.get_children():
            self.tree.delete(item)

    def _show_notes(self, notes):
        self.notes.configure(state="normal")
        self.notes.delete("1.0", "end")
        self.notes.insert("end", "\n".join(f"• {n}" for n in notes))
        self.notes.configure(state="disabled")

    def _set_status(self, text: str, error: bool = False):
        self.status.configure(text=text, foreground="#b00020" if error else "#555")


def main():
    if _HAS_DND:
        root = TkinterDnD.Tk()
    else:
        root = tk.Tk()
    CurveApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
