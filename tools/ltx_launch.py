"""Bootstrap launcher for the LTX-2 backend server.

The bundled LTX Desktop Python is an *embeddable* distribution (it has a
``python._pth`` file), so it ignores PYTHONPATH and does not add a script's own
directory to ``sys.path``. LTX Desktop works around this with an explicit
``sys.path.insert`` before running the server; we do the same here so the
``services`` / ``state`` / ``app_factory`` packages resolve.

Config is taken from environment variables (set by run-ltx-server.ps1):
    LTX_APP_DATA_DIR (required), LTX_PORT (optional). LTX_AUTH_TOKEN is left
    unset so the auth middleware stays disabled (localhost only).
"""
import os
import runpy
import sys

BACKEND = r"C:\Users\keith\AppData\Local\Programs\LTX Desktop\resources\backend"
SERVER = os.path.join(BACKEND, "ltx2_server.py")

sys.path.insert(0, BACKEND)
runpy.run_path(SERVER, run_name="__main__")
