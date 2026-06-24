# Remote Access

How to remote desktop into the home PC that runs KeithVision / KeithUI.

The machine is **Windows 11/10 Pro** with an **NVIDIA GPU**, so this uses a
two-tool setup, both reached **securely with no ports exposed to the internet**:

- **Tailscale + Microsoft RDP** — everyday secure path for admin / file work.
- **Parsec** — for GPU-heavy, low-latency interactive use (previewing the video
  pipeline, editing), where plain RDP feels laggy and doesn't use the GPU well.

> **Golden rule:** never port-forward RDP (TCP 3389) directly to the internet —
> it gets scanned and brute-forced within minutes. Keep it behind Tailscale.

---

## Setup A — Tailscale + RDP (primary, secure)

### On the home PC (the host)

1. **Enable Remote Desktop:** Settings → System → Remote Desktop → toggle **On**.
   Note the PC name shown there. Ensure the Windows account has a **password**
   (RDP refuses blank passwords).
2. **Install Tailscale:** https://tailscale.com → sign in (Google / Microsoft /
   GitHub). After it connects, note the machine's **Tailscale IP** (`100.x.y.z`)
   or its MagicDNS name (e.g. `home-pc.tailnet-xxxx.ts.net`).
3. **Stop it sleeping:** Settings → System → Power → **Sleep = Never** (plugged
   in). RDP/Tailscale can't wake a sleeping machine on their own.

### On the device you connect from

Clients: Windows "Remote Desktop Connection", macOS "Windows App" (formerly
Microsoft Remote Desktop), or the iOS/Android Remote Desktop client.

4. Install **Tailscale**, sign in with the **same account** → both devices are
   now on one private, encrypted network.
5. Open the RDP client → connect to the host's **Tailscale IP or MagicDNS name**
   → log in with the Windows username / password.

Traffic rides Tailscale's WireGuard tunnel end-to-end; **port 3389 is never
exposed to the internet.**

---

## Setup B — Parsec (GPU-heavy / interactive)

1. On the home PC: install **Parsec** (https://parsec.app), sign in, and enable
   **"Run Parsec when Windows starts"** / host mode so it's reachable at the
   login screen.
2. On the client: install Parsec, sign in with the **same account**, pick the
   home PC from the list, connect.

Parsec encodes the desktop with the GPU's **NVENC** for near-local latency —
exactly what RDP is bad at. Ideal for eyeballing video output from the pipeline.

---

## Good to know

- **Check the Windows edition:** `Win+R` → `winver`. Native RDP hosting needs
  **Pro** (Home can't host RDP without a workaround).
- **Waking a sleeping PC later:** Tailscale has a Wake-on-LAN feature, but it
  needs *another* device already online on the home LAN to send the magic
  packet. Simplest for now is "don't sleep."
- **Network Level Authentication** (NLA) is on by default for RDP — leave it on.
- **Optional hardening:** Tailscale ACLs can restrict which of your devices may
  reach the host, and Tailscale SSH / tailnet lock add more control if wanted.

---

## Reaching the KeithUI web app remotely (optional)

KeithUI (and KeithVision) bind to **`127.0.0.1` only** by design — a
localhost-only trust model (see the controller comments), which is why there's
no auth on the admin/studio pages. So out of the box, the web UI is **not**
reachable over the tailnet; you reach it by RDP'ing in and using the browser on
the host.

If you want true browser access from another device (e.g. a phone) over
Tailscale, the Kestrel binding has to listen on the tailnet interface as well.
**Do not** just bind `0.0.0.0` — that drops the localhost-only assumption the
"no auth" design relies on. The safe shape is:

- Bind Kestrel to the host's **Tailscale IP** (`100.x.y.z`) specifically, so
  only tailnet devices can reach it, **and/or**
- Put authentication in front of it before widening the binding at all.

This is a deliberate change, not a default — ask and it can be wired up properly
(configurable binding + a note in the trust-model comments).
